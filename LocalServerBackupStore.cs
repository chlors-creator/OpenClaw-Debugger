using System.IO;
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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var staging = Path.Combine(RootDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var archiveName = "server-rootfs.tar.gz";
        var archivePath = Path.Combine(staging, archiveName);
        Directory.CreateDirectory(staging);

        try
        {
            progress?.Report(new ServerSnapshotProgress("estimating", 0, null, null, null, 1, MaximumAttempts));
            var estimatedTotalBytes = await EstimateWithRetryAsync(settings, remote, progress, cancellationToken);
            progress?.Report(new ServerSnapshotProgress("transferring", 0, estimatedTotalBytes, null, null, 1, MaximumAttempts));

            RemoteSnapshotTransferResult? transfer = null;
            var transferAttempt = 1;
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                transferAttempt = attempt;
                try
                {
                    progress?.Report(new ServerSnapshotProgress("transferring", 0, estimatedTotalBytes, null, null, attempt, MaximumAttempts));
                    await using var output = new FileStream(
                        archivePath, FileMode.Create, FileAccess.Write, FileShare.None,
                        256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    transfer = await remote.WriteServerSnapshotAsync(
                        settings, output, estimatedTotalBytes,
                        new AttemptProgress(progress, attempt, MaximumAttempts), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    break;
                }
                catch (SnapshotTransferInterruptedException) when (attempt < MaximumAttempts)
                {
                    var delay = RetryDelay(attempt);
                    progress?.Report(new ServerSnapshotProgress(
                        "retrying", 0, estimatedTotalBytes, null, null, attempt + 1, MaximumAttempts,
                        $"传输连接中断；{delay.TotalSeconds:0} 秒后从头重传当前快照。"));
                    await Task.Delay(delay, cancellationToken);
                }
                catch (SnapshotTransferInterruptedException ex)
                {
                    throw new IOException(
                        $"传输阶段网络连续中断 {MaximumAttempts} 次；未完成的本机临时文件已清理。请检查网络后重新备份。{Environment.NewLine}{ex.Message}", ex);
                }
            }

            if (transfer is null)
                throw new InvalidOperationException("服务器快照传输未能完成。");

            var created = DateTimeOffset.Now;
            var manifest = new ServerSnapshotManifest(
                1,
                settings.Host,
                settings.Username,
                created,
                archiveName,
                transfer.Bytes,
                transfer.Sha256,
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
                "completed", transfer.Bytes, transfer.Bytes, null, 0, transferAttempt, MaximumAttempts));
            return new ServerSnapshotResult(
                finalDirectory,
                Path.Combine(finalDirectory, archiveName),
                transfer.Bytes,
                transfer.Sha256);
        }
        catch
        {
            DeleteStagingDirectory(staging);
            throw;
        }
    }

    private static async Task<long> EstimateWithRetryAsync(
        ConnectionSettings settings,
        RemoteOpenClawClient remote,
        IProgress<ServerSnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                    progress?.Report(new ServerSnapshotProgress("estimating", 0, null, null, null, attempt, MaximumAttempts));
                return await remote.EstimateServerSnapshotSizeAsync(settings, cancellationToken);
            }
            catch (SnapshotTransferInterruptedException) when (attempt < MaximumAttempts)
            {
                var delay = RetryDelay(attempt);
                progress?.Report(new ServerSnapshotProgress(
                    "retrying", 0, null, null, null, attempt + 1, MaximumAttempts,
                    $"估算期间 SSH 连接中断；{delay.TotalSeconds:0} 秒后重试。"));
                await Task.Delay(delay, cancellationToken);
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
