using System.IO;
using System.IO.Compression;
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
    private const long MinimumFreeSpaceBytes = 512L * 1024 * 1024;
    public string RootDirectory => Path.GetFullPath(rootDirectory);

    public async Task<ServerSnapshotResult> CreateAsync(
        ConnectionSettings settings,
        IRemoteSnapshotClient remote,
        IProgress<ServerSnapshotProgress>? progress = null,
        BackupPauseController? pauseController = null,
        CancellationToken cancellationToken = default,
        int retentionCount = 5)
    {
        Directory.CreateDirectory(RootDirectory);
        EnsureLocalDiskSpace(0);
        var staging = Path.Combine(RootDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var archiveName = "server-rootfs.tar.gz";
        var archivePath = Path.Combine(staging, archiveName);
        var archivePartPath = archivePath + ".part";
        var remoteArchive = "/tmp/.openclaw-debugger-" + Guid.NewGuid().ToString("N") + ".tar.gz";
        Directory.CreateDirectory(staging);

        try
        {
            progress?.Report(new ServerSnapshotProgress("preparing", 0, null, null, null, 1, MaximumAttempts,
                "正在服务器端生成可断点读取的快照归档"));
            OperationLogStore.Current?.Stage("preparing", "正在服务器端生成快照");
            if (pauseController is not null)
                await pauseController.WaitIfPausedAsync(cancellationToken);
            await CreateRemoteSnapshotWithRetryAsync(settings, remote, remoteArchive, progress, pauseController, cancellationToken);
            if (pauseController is not null)
                await pauseController.WaitIfPausedAsync(cancellationToken);
            var totalBytes = await remote.GetServerSnapshotSizeAsync(settings, remoteArchive, cancellationToken);
            EnsureLocalDiskSpace(totalBytes);
            progress?.Report(new ServerSnapshotProgress("transferring", 0, totalBytes, null, null, 1, MaximumAttempts,
                "快照已生成，开始传输；网络中断后会从本机临时文件长度继续"));
            OperationLogStore.Current?.Stage("transferring", $"开始传输快照，共 {totalBytes} 字节");

            RemoteSnapshotTransferResult? transfer = null;
            var transferAttempt = 1;
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                transferAttempt = attempt;
                try
                {
                    if (pauseController is not null)
                        await pauseController.WaitIfPausedAsync(cancellationToken);
                    var existingBytes = File.Exists(archivePartPath) ? new FileInfo(archivePartPath).Length : 0;
                    if (existingBytes > totalBytes)
                    {
                        File.Delete(archivePartPath);
                        existingBytes = 0;
                    }
                    progress?.Report(new ServerSnapshotProgress("transferring", existingBytes, totalBytes, null, null, attempt, MaximumAttempts,
                        existingBytes > 0 ? $"从 {FormatBytes(existingBytes)} 继续传输" : null));
                    await using var output = new FileStream(
                        archivePartPath, existingBytes > 0 ? FileMode.OpenOrCreate : FileMode.Create,
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
                    var retained = File.Exists(archivePartPath) ? new FileInfo(archivePartPath).Length : 0;
                    var delay = RetryDelay(attempt);
                    progress?.Report(new ServerSnapshotProgress(
                        "retrying", retained, totalBytes, null, null, attempt + 1, MaximumAttempts,
                        $"传输连接中断；保留 {FormatBytes(retained)}，{delay.TotalSeconds:0} 秒后继续。"));
                    OperationLogStore.Current?.Stage("retrying", "传输连接中断，保留已接收数据后继续", attempt + 1);
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

            var archiveBytes = new FileInfo(archivePartPath).Length;
            progress?.Report(new ServerSnapshotProgress("verifying", archiveBytes, totalBytes, null, null, transferAttempt, MaximumAttempts,
                "正在验证 gzip 压缩包完整性"));
            OperationLogStore.Current?.Stage("verifying", "正在验证本地压缩包");
            await VerifyArchiveAsync(archivePartPath, cancellationToken);
            File.Move(archivePartPath, archivePath, overwrite: false);
            archiveBytes = new FileInfo(archivePath).Length;
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
            var manifestPath = Path.Combine(staging, "snapshot-manifest.json");
            var manifestTemporary = manifestPath + ".tmp";
            await File.WriteAllTextAsync(manifestTemporary, manifestJson, new UTF8Encoding(false), cancellationToken);
            File.Move(manifestTemporary, manifestPath, overwrite: false);

            var backupName = $"{created:yyyyMMdd-HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            var finalDirectory = Path.Combine(RootDirectory, backupName);
            Directory.Move(staging, finalDirectory);
            progress?.Report(new ServerSnapshotProgress("finalizing", archiveBytes, archiveBytes, null, null, transferAttempt, MaximumAttempts,
                "快照已校验，正在清理旧备份"));
            OperationLogStore.Current?.Stage("finalizing", "正在原子提交快照并清理旧备份");
            CleanupRetention(retentionCount);
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
        IRemoteSnapshotClient remote,
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

    private static async Task VerifyArchiveAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
        var buffer = new byte[256 * 1024];
        var total = 0L;
        while (true)
        {
            var read = await gzip.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (total > long.MaxValue - read) throw new InvalidDataException("压缩包解压大小溢出。");
            total += read;
        }
    }

    private void EnsureLocalDiskSpace(long archiveBytes)
    {
        var root = Path.GetPathRoot(RootDirectory);
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("无法确定备份目录所在磁盘。");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("备份目录所在磁盘当前不可用。");
        var required = Math.Max(0, archiveBytes) + MinimumFreeSpaceBytes;
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"本地磁盘空间不足：至少需要 {FormatBytes(required)}，当前可用 {FormatBytes(drive.AvailableFreeSpace)}。");
        OperationLogStore.Current?.Stage("disk-preflight", $"本地可用空间 {FormatBytes(drive.AvailableFreeSpace)}，预计新增 {FormatBytes(Math.Max(0, archiveBytes))}");
    }

    public static int CleanupStagingDirectories(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root)) return 0;
        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, ".staging-*", SearchOption.TopDirectoryOnly))
        {
            if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch
            {
                // A backup that is still being written is retained until its owner exits.
            }
        }
        OperationLogStore.Current?.Stage("backup-staging-cleanup", $"清理残留备份临时目录：{deleted} 个");
        return deleted;
    }

    private void CleanupRetention(int retentionCount)
    {
        var keep = Math.Clamp(retentionCount, 1, 30);
        DirectoryInfo[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(RootDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.OrdinalIgnoreCase))
                .Where(path => (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) == 0)
                .Where(path => File.Exists(Path.Combine(path, "server-rootfs.tar.gz")) &&
                              File.Exists(Path.Combine(path, "snapshot-manifest.json")))
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.CreationTimeUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            OperationLogStore.Current?.Stage("backup-retention-failed", ex.Message);
            return;
        }
        foreach (var directory in directories.Skip(keep))
        {
            try { directory.Delete(recursive: true); }
            catch { }
        }
        OperationLogStore.Current?.Stage("backup-retention", $"保留最近 {keep} 份备份，清理 {Math.Max(0, directories.Length - keep)} 份旧备份");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KiB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MiB";
        return (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GiB";
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
