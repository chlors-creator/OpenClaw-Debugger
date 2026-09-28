using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed record ServerSnapshotManifest(
    int SchemaVersion,
    string Host,
    string Username,
    DateTimeOffset CreatedLocal,
    string ArchiveFileName,
    long ArchiveBytes,
    string ArchiveSha256,
    string Scope,
    IReadOnlyList<string> ExcludedPaths,
    string Consistency);

public sealed record ServerSnapshotResult(string Directory, string ArchivePath, long ArchiveBytes, string Sha256);

public sealed class LocalServerBackupStore(string rootDirectory)
{
    private const int MaximumAttempts = 8;
    public string RootDirectory => Path.GetFullPath(rootDirectory);

    public async Task<ServerSnapshotResult> CreateAsync(
        ConnectionSettings settings,
        RemoteOpenClawClient remote,
        IProgress<ServerSnapshotProgress>? progress = null,
        BackupPauseController? pauseController = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var staging = Path.Combine(RootDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var archiveName = "server-rootfs.tar.gz";
        var archivePath = Path.Combine(staging, archiveName);
        var remoteArchive = "/tmp/.openclaw-debugger-" + Guid.NewGuid().ToString("N") + ".tar.gz";
        Directory.CreateDirectory(staging);

        try
        {
            progress?.Report(new ServerSnapshotProgress("preparing", 0, null, null, null, 1, MaximumAttempts,
                "正在服务器端生成可断点读取的快照归档"));
            if (pauseController is not null)
                await pauseController.WaitIfPausedAsync(cancellationToken);
            await CreateRemoteSnapshotWithRetryAsync(settings, remote, remoteArchive, progress, pauseController, cancellationToken);
            if (pauseController is not null)
                await pauseController.WaitIfPausedAsync(cancellationToken);
            var totalBytes = await remote.GetServerSnapshotSizeAsync(settings, remoteArchive, cancellationToken);
            progress?.Report(new ServerSnapshotProgress("transferring", 0, totalBytes, null, null, 1, MaximumAttempts,
                "快照已生成，开始传输；网络中断后会从本机临时文件长度继续"));

            RemoteSnapshotTransferResult? transfer = null;
            var transferAttempt = 1;
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                transferAttempt = attempt;
                try
                {
                    if (pauseController is not null)
                        await pauseController.WaitIfPausedAsync(cancellationToken);
                    var existingBytes = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
                    if (existingBytes > totalBytes)
                    {
                        File.Delete(archivePath);
                        existingBytes = 0;
                    }
                    progress?.Report(new ServerSnapshotProgress("transferring", existingBytes, totalBytes, null, null, attempt, MaximumAttempts,
                        existingBytes > 0 ? $"从 {FormatBytes(existingBytes)} 继续传输" : null));
                    await using var output = new FileStream(
                        archivePath, existingBytes > 0 ? FileMode.OpenOrCreate : FileMode.Create,
                        FileAccess.ReadWrite, FileShare.Read,
                        256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    output.Position = existingBytes;
                    transfer = await remote.ResumeServerSnapshotAsync(
                        settings, remoteArchive, output, existingBytes, totalBytes,
                        pauseController,
                        new AttemptProgress(progress, attempt, MaximumAttempts), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    if (output.Length != totalBytes)
                        throw new SnapshotTransferInterruptedException("本机快照临时文件长度与服务器归档不一致。");
                    break;
                }
                catch (SnapshotTransferInterruptedException) when (attempt < MaximumAttempts)
                {
                    var retained = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
                    var delay = RetryDelay(attempt);
                    progress?.Report(new ServerSnapshotProgress(
                        "retrying", retained, totalBytes, null, null, attempt + 1, MaximumAttempts,
                        $"传输连接中断；保留 {FormatBytes(retained)}，{delay.TotalSeconds:0} 秒后继续。"));
                    await Task.Delay(delay, cancellationToken);
                }
                catch (SnapshotTransferInterruptedException ex)
                {
                    throw new IOException(
                        $"传输阶段网络连续中断 {MaximumAttempts} 次；本次任务将清理临时文件。请检查网络后重新备份。{Environment.NewLine}{ex.Message}", ex);
                }
            }

            if (transfer is null)
                throw new InvalidOperationException("服务器快照传输未能完成。");

            var archiveBytes = new FileInfo(archivePath).Length;
            if (archiveBytes != totalBytes)
                throw new InvalidDataException("本机快照归档大小校验失败。");
            var archiveSha256 = await ComputeSha256Async(archivePath, cancellationToken);
            var created = DateTimeOffset.Now;
            var manifest = new ServerSnapshotManifest(
                1,
                settings.Host,
                settings.Username,
                created,
                archiveName,
                archiveBytes,
                archiveSha256,
                "Tar archive of the server root filesystem (/), including mounted filesystems.",
                ["/proc", "/sys", "/dev", "/run"],
                "Live file-level capture; files may change while being read. This is not an atomic disk or volume snapshot.");
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(staging, "snapshot-manifest.json"), manifestJson, new UTF8Encoding(false), cancellationToken);

            var backupName = $"{created:yyyyMMdd-HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            var finalDirectory = Path.Combine(RootDirectory, backupName);
            Directory.Move(staging, finalDirectory);
            progress?.Report(new ServerSnapshotProgress(
                "completed", archiveBytes, archiveBytes, null, 0, transferAttempt, MaximumAttempts));
            return new ServerSnapshotResult(
                finalDirectory,
                Path.Combine(finalDirectory, archiveName),
                archiveBytes,
                archiveSha256);
        }
        catch
        {
            DeleteStagingDirectory(staging);
            throw;
        }
        finally
        {
            await remote.RemoveServerSnapshotAsync(settings, remoteArchive);
        }
    }

    private static async Task CreateRemoteSnapshotWithRetryAsync(
        ConnectionSettings settings,
        RemoteOpenClawClient remote,
        string remoteArchive,
        IProgress<ServerSnapshotProgress>? progress,
        BackupPauseController? pauseController,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                if (pauseController is not null) await pauseController.WaitIfPausedAsync(cancellationToken);
                await remote.RemoveServerSnapshotAsync(settings, remoteArchive, cancellationToken);
                progress?.Report(new ServerSnapshotProgress("preparing", 0, null, null, null, attempt, MaximumAttempts,
                    attempt > 1 ? "服务器快照生成重试中" : "正在服务器端打包所有文件"));
                await remote.CreateServerSnapshotAsync(settings, remoteArchive, pauseController, cancellationToken);
                return;
            }
            catch (SnapshotTransferInterruptedException) when (attempt < MaximumAttempts)
            {
                var delay = RetryDelay(attempt);
                progress?.Report(new ServerSnapshotProgress("retrying", 0, null, null, null, attempt + 1, MaximumAttempts,
                    $"生成快照时 SSH 连接中断；{delay.TotalSeconds:0} 秒后重试。"));
                await Task.Delay(delay, cancellationToken);
            }
        }
        throw new IOException($"服务器快照生成连续失败 {MaximumAttempts} 次。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KiB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MiB";
        return (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GiB";
    }

    private static async Task<long> EstimateWithRetryAsync(
        ConnectionSettings settings,
        RemoteOpenClawClient remote,
        IProgress<ServerSnapshotProgress>? progress,
        BackupPauseController? pauseController,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                if (pauseController is not null)
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                if (attempt > 1)
                    progress?.Report(new ServerSnapshotProgress("estimating", 0, null, null, null, attempt, MaximumAttempts));
                var estimate = await remote.EstimateServerSnapshotSizeAsync(settings, cancellationToken);
                if (pauseController is not null)
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                return estimate;
            }
            catch (SnapshotTransferInterruptedException) when (attempt < MaximumAttempts)
            {
                var delay = RetryDelay(attempt);
                progress?.Report(new ServerSnapshotProgress(
                    "retrying", 0, null, null, null, attempt + 1, MaximumAttempts,
                    $"估算期间 SSH 连接中断；{delay.TotalSeconds:0} 秒后重试。"));
                await Task.Delay(delay, cancellationToken);
                if (pauseController is not null)
                    await pauseController.WaitIfPausedAsync(cancellationToken);
            }
            catch (SnapshotTransferInterruptedException ex)
            {
                throw new IOException(
                    $"估算阶段网络连续中断 {MaximumAttempts} 次；已停止自动重试。{Environment.NewLine}{ex.Message}", ex);
            }
        }
        throw new InvalidOperationException("快照总大小估算未能完成。");
    }

    private static TimeSpan RetryDelay(int failedAttempt)
    {
        var seconds = Math.Min(30, 2 * Math.Pow(2, Math.Max(0, failedAttempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class AttemptProgress(
        IProgress<ServerSnapshotProgress>? destination,
        int attempt,
        int maximumAttempts) : IProgress<ServerSnapshotProgress>
    {
        public void Report(ServerSnapshotProgress value) =>
            destination?.Report(value with { Attempt = attempt, MaxAttempts = maximumAttempts });
    }

    private void DeleteStagingDirectory(string staging)
    {
        var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(staging);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(full)) return;
        try { Directory.Delete(full, recursive: true); }
        catch { }
    }
}
