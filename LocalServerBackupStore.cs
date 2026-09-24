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
    public string RootDirectory => Path.GetFullPath(rootDirectory);

    public async Task<ServerSnapshotResult> CreateAsync(
        ConnectionSettings settings,
        RemoteOpenClawClient remote,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var staging = Path.Combine(RootDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        var archiveName = "server-rootfs.tar.gz";
        var archivePath = Path.Combine(staging, archiveName);
        Directory.CreateDirectory(staging);

        try
        {
            RemoteSnapshotTransferResult transfer;
            await using (var output = new FileStream(
                archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                transfer = await remote.WriteServerSnapshotAsync(settings, output, progress, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

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
