using System.IO;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed record StickerUiRow(string Id, string ImagePath, string TagsText, double Weight, bool Catalogued);

public sealed record StickerLoadResult(
    IReadOnlyList<StickerUiRow> StickerFiles,
    int StickerCount,
    bool StickerEditingEnabled,
    string? CatalogText,
    string? ManifestText,
    string StickerStatus);

public sealed class StickerService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRemoteFileClient _filesRemote;
    private readonly IRemoteStickerClient _stickersRemote;
    private readonly StickerThumbnailCache _thumbnailCache;
    private readonly StickerUploadService _uploads;
    private readonly Func<ConnectionSettings> _connection;
    private readonly Func<LocalSnapshotStore?> _snapshots;
    private readonly Func<string> _privateDirectory;
    private readonly Func<bool> _isConnected;
    private readonly Func<bool> _isBusy;
    private readonly Action<bool> _setConnected;
    private readonly Action<bool> _setBusy;

    private IReadOnlyList<RemoteFile> _files = [];
    private RemoteFile? _catalogFile;
    private RemoteFile? _manifestFile;
    private RemoteFileContent? _catalogContent;
    private RemoteFileContent? _manifestContent;
    private ParsedStickerCatalog? _catalog;

    public StickerService(
        IRemoteFileClient filesRemote,
        IRemoteStickerClient stickersRemote,
        StickerThumbnailCache thumbnailCache,
        StickerUploadService uploads,
        Func<ConnectionSettings> connection,
        Func<LocalSnapshotStore?> snapshots,
        Func<string> privateDirectory,
        Func<bool> isConnected,
        Func<bool> isBusy,
        Action<bool> setConnected,
        Action<bool> setBusy)
    {
        _filesRemote = filesRemote;
        _stickersRemote = stickersRemote;
        _thumbnailCache = thumbnailCache;
        _uploads = uploads;
        _connection = connection;
        _snapshots = snapshots;
        _privateDirectory = privateDirectory;
        _isConnected = isConnected;
        _isBusy = isBusy;
        _setConnected = setConnected;
        _setBusy = setBusy;
    }

    public IReadOnlyList<RemoteFile> Files => _files;

    public async Task<StickerLoadResult> LoadAsync(IReadOnlyList<RemoteFile> files, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        _files = files;
        _catalogFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "catalog.json");
        _manifestFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "MANIFEST.md");
        _catalog = null;
        _catalogContent = null;
        _manifestContent = null;

        string stickerStatus;
        if (_catalogFile is null || _manifestFile is null)
        {
            stickerStatus = "找不到 catalog.json 或 MANIFEST.md；标签编辑已关闭以避免不完整写入。";
        }
        else
        {
            try
            {
                var catTask = _filesRemote.ReadAsync(_connection(), _catalogFile, cancellationToken);
                var manifestTask = _filesRemote.ReadAsync(_connection(), _manifestFile, cancellationToken);
                await Task.WhenAll(catTask, manifestTask);
                _catalogContent = catTask.Result;
                _manifestContent = manifestTask.Result;
                var images = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
                if (StickerCatalogEditor.TryParse(_catalogContent.Text ?? "", images, out _catalog, out var error))
                    stickerStatus = "已读取 " + _catalog!.Rows.Count + " 条目录记录。标签保存会同时更新目录和说明文件，并在写入前生成加密快照。";
                else
                    stickerStatus = error;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                stickerStatus = "读取目录失败：" + ex.Message;
            }
        }

        var imageCount = _files.Count(x => x.Root == "stickers" && x.IsImage);
        return new StickerLoadResult(
            GetUiRows(),
            imageCount,
            _catalog is not null && _catalogFile is not null && _manifestFile is not null,
            _catalogContent?.Text,
            _manifestContent?.Text,
            stickerStatus);
    }

    public async Task<object> ReadAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var path = payload.GetProperty("path").GetString() ?? "";
        var file = FindImage(path);
        var content = await _filesRemote.ReadAsync(_connection(), file, cancellationToken);
        if (content.Binary is null) throw new InvalidDataException("服务器返回的图片数据为空。");
        var mime = Path.GetExtension(file.RelativePath).ToLowerInvariant() switch
        {
            ".gif" => "image/gif", ".png" => "image/png", ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg", ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
        return new { path, dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(content.Binary), size = content.Size };
    }

    public async Task<object> ReadThumbnailAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var path = payload.GetProperty("path").GetString() ?? "";
        var file = FindImage(path);
        return await _thumbnailCache.ReadAsync(_filesRemote, _connection(), file, cancellationToken);
    }

    public object PreviewRows(JsonElement payload)
    {
        EnsureConnected();
        EnsureCatalogLoaded();
        var edits = payload.GetProperty("rows").Deserialize<List<StickerEditRow>>(JsonOptions) ?? [];
        var byKey = edits.ToDictionary(x => x.Id + "\0" + x.ImagePath, StringComparer.OrdinalIgnoreCase);
        var oldTags = _catalog!.Rows.Select(x => x.TagsText).ToArray();
        var oldWeights = _catalog.Rows.Select(x => x.Weight).ToArray();
        string newCatalog;
        string newManifest;
        try
        {
            for (var i = 0; i < _catalog.Rows.Count; i++)
            {
                var row = _catalog.Rows[i];
                if (!byKey.TryGetValue(row.Id + "\0" + row.ImagePath, out var edit))
                    throw new InvalidDataException("标签列表结构已变化，请重新扫描。");
                if (edit.TagsText.Length > 4000) throw new InvalidDataException("单条标签不能超过 4000 个字符。");
                var weight = edit.Weight ?? row.Weight;
                ValidateWeight(weight);
                row.TagsText = edit.TagsText;
                row.Weight = weight;
            }
            newCatalog = _catalog.SerializeWithEdits();
            using var _ = JsonDocument.Parse(newCatalog);
            if (!StickerManifestSynchronizer.TryUpdate(_manifestContent!.Text!, _catalog.Rows, out newManifest, out var error))
                throw new InvalidDataException(error);
        }
        finally
        {
            for (var i = 0; i < _catalog.Rows.Count; i++)
            {
                _catalog.Rows[i].TagsText = oldTags[i];
                _catalog.Rows[i].Weight = oldWeights[i];
            }
        }
        return new
        {
            before = "catalog.json\n" + _catalogContent!.Text + "\n\n----- MANIFEST.md -----\n" + _manifestContent!.Text,
            after = "catalog.json\n" + newCatalog + "\n\n----- MANIFEST.md -----\n" + newManifest
        };
    }

    public async Task<object> SaveRowsAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        EnsureCatalogLoaded();
        var rows = payload.GetProperty("rows").Deserialize<List<StickerEditRow>>(JsonOptions) ?? [];
        var byKey = rows.ToDictionary(x => x.Id + "\0" + x.ImagePath, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _catalog!.Rows)
        {
            if (!byKey.TryGetValue(row.Id + "\0" + row.ImagePath, out var edit))
                throw new InvalidDataException("标签列表结构已变化，请重新扫描。");
            if (edit.TagsText.Length > 4000) throw new InvalidDataException("单条标签不能超过 4000 个字符。");
            var weight = edit.Weight ?? row.Weight;
            ValidateWeight(weight);
            row.TagsText = edit.TagsText;
            row.Weight = weight;
        }
        var updatedCatalog = _catalog.SerializeWithEdits();
        using var _ = JsonDocument.Parse(updatedCatalog);
        if (_catalogContent?.Text is null || _manifestContent?.Text is null)
            throw new InvalidOperationException("标签原始内容没有加载。");
        if (!StickerManifestSynchronizer.TryUpdate(_manifestContent.Text, _catalog.Rows, out var updatedManifest, out var error))
            throw new InvalidDataException(error);
        return await SavePairAsync(updatedCatalog, updatedManifest, cancellationToken);
    }

    public async Task<object> SaveRawAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_catalogContent?.Text is null || _manifestContent?.Text is null)
            throw new InvalidOperationException("请先读取表情包目录。");
        var catalog = payload.GetProperty("catalog").GetString() ?? "";
        var manifest = payload.GetProperty("manifest").GetString() ?? "";
        using var _ = JsonDocument.Parse(catalog);
        return await SavePairAsync(catalog, manifest, cancellationToken);
    }

    private async Task<object> SavePairAsync(string updatedCatalog, string updatedManifest, CancellationToken cancellationToken = default)
    {
        if (_catalogFile is null || _manifestFile is null || _catalogContent is null || _manifestContent is null || _snapshots() is null)
            throw new InvalidOperationException("标签文件或快照存储未就绪。");
        var oldCatalog = _catalogContent;
        var oldManifest = _manifestContent;
        if (updatedCatalog == oldCatalog.Text && updatedManifest == oldManifest.Text)
            return new
            {
                changed = false,
                snapshotCount = 0,
                stickerFiles = GetUiRows(),
                stickerEditingEnabled = _catalog is not null,
                catalogText = oldCatalog.Text,
                manifestText = oldManifest.Text
            };

        _setBusy(true);
        try
        {
            var catTask = _filesRemote.ReadAsync(_connection(), _catalogFile, cancellationToken);
            var manifestTask = _filesRemote.ReadAsync(_connection(), _manifestFile, cancellationToken);
            await Task.WhenAll(catTask, manifestTask);
            var latestCatalog = catTask.Result;
            var latestManifest = manifestTask.Result;
            if (latestCatalog.Sha256 != oldCatalog.Sha256 || latestManifest.Sha256 != oldManifest.Sha256)
                throw new RemoteConflictException("服务器上的目录文件已发生变化。没有覆盖；请重新扫描后再编辑。");

            var snapshots = _snapshots()!;
            var snapshotCount = 0;
            if (updatedCatalog != oldCatalog.Text)
            {
                await snapshots.SaveAsync("stickers", "catalog.json", oldCatalog.RawBytes, oldCatalog.Sha256);
                snapshotCount++;
            }
            if (updatedManifest != oldManifest.Text)
            {
                await snapshots.SaveAsync("stickers", "MANIFEST.md", oldManifest.RawBytes, oldManifest.Sha256);
                snapshotCount++;
            }
            var pair = await _stickersRemote.WriteStickerPairAsync(
                _connection(), updatedCatalog, oldCatalog.Sha256, updatedManifest, oldManifest.Sha256, cancellationToken);
            var catalogBytes = new UTF8Encoding(false).GetBytes(updatedCatalog);
            var manifestBytes = new UTF8Encoding(false).GetBytes(updatedManifest);
            _catalogContent = oldCatalog with
            {
                Text = updatedCatalog, RawBytes = catalogBytes, Sha256 = pair.CatalogSha256,
                Size = pair.CatalogSize, ModifiedUtc = DateTimeOffset.UtcNow
            };
            _manifestContent = oldManifest with
            {
                Text = updatedManifest, RawBytes = manifestBytes, Sha256 = pair.ManifestSha256,
                Size = pair.ManifestSize, ModifiedUtc = DateTimeOffset.UtcNow
            };
            var images = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
            StickerCatalogEditor.TryParse(updatedCatalog, images, out _catalog, out _);
            return new
            {
                changed = true,
                snapshotCount,
                stickerFiles = GetUiRows(),
                stickerEditingEnabled = _catalog is not null,
                catalogText = _catalogContent.Text,
                manifestText = _manifestContent.Text
            };
        }
        finally { _setBusy(false); }
    }

    public async Task<object> RenameAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_isBusy()) throw new InvalidOperationException("当前有操作正在进行。");
        EnsureCatalogLoaded();
        var oldName = payload.GetProperty("oldFileName").GetString() ?? "";
        var newName = payload.GetProperty("newFileName").GetString() ?? "";
        StickerUploadService.ValidateFileName(newName);
        if (oldName.Equals(newName, StringComparison.Ordinal)) throw new InvalidDataException("新文件名与当前文件名相同。");
        if (!Path.GetExtension(oldName).Equals(Path.GetExtension(newName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("重命名不能改变图片格式或扩展名。");

        var oldFile = FindImage(oldName);
        if (_files.Any(x => x.Root == "stickers" && x.IsImage && x.RelativePath.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("服务器已存在同名图片。");

        var oldCatalogText = _catalogContent!.Text!;
        var manifestSource = _manifestContent!.Text!;
        var oldRow = _catalog!.Rows.FirstOrDefault(x => x.ImagePath.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (oldRow is null)
        {
            var existingRows = _catalog.Rows.ToList();
            if (!_catalog.TryAddImage(oldName, out var registeredRow, out var registerError))
            {
                RestoreCatalog();
                throw new InvalidDataException(registerError);
            }
            if (!StickerManifestSynchronizer.TryAppendRow(manifestSource, existingRows, registeredRow,
                out var registeredManifest, out var registerManifestError))
            {
                RestoreCatalog();
                throw new InvalidDataException(registerManifestError);
            }
            manifestSource = registeredManifest;
            oldRow = registeredRow;
        }
        if (!_catalog.TryRenameImage(oldName, newName, out var newRow, out var catalogError))
        {
            RestoreCatalog();
            throw new InvalidDataException(catalogError);
        }

        var updatedRows = _catalog.Rows.ToList();
        var updatedCatalog = _catalog.SerializeWithEdits();
        if (!StickerManifestSynchronizer.TryRenameReferences(manifestSource, oldRow, newRow, updatedRows,
            out var updatedManifest, out var manifestError))
        {
            RestoreCatalog();
            throw new InvalidDataException(manifestError);
        }

        var plannedFiles = _files.Select(file => file.Key == oldFile.Key
            ? new RemoteFile
            {
                Root = file.Root, RelativePath = newName, Kind = file.Kind, Size = file.Size,
                Sha256 = file.Sha256, ModifiedUtc = file.ModifiedUtc, Editable = file.Editable
            }
            : file).ToList();
        if (!StickerCatalogEditor.TryParse(updatedCatalog, plannedFiles.Where(x => x.Root == "stickers" && x.IsImage).ToList(),
            out _, out var parseError))
        {
            RestoreCatalog();
            throw new InvalidDataException("不能安全重命名：" + parseError);
        }

        _setBusy(true);
        try
        {
            var currentImage = await _filesRemote.ReadAsync(_connection(), oldFile, cancellationToken);
            var moved = await _stickersRemote.RenameStickerAsync(_connection(), oldName, newName, currentImage.Sha256, cancellationToken);
            _files = plannedFiles.Select(file => file.Root == "stickers" && file.RelativePath == newName
                ? new RemoteFile
                {
                    Root = file.Root, RelativePath = file.RelativePath, Kind = file.Kind, Size = moved.Size,
                    Sha256 = moved.Sha256, ModifiedUtc = DateTimeOffset.UtcNow, Editable = false
                }
                : file).ToList();

            try { await SavePairAsync(updatedCatalog, updatedManifest, cancellationToken); }
            catch (Exception saveError)
            {
                try
                {
                    await _stickersRemote.RenameStickerAsync(_connection(), newName, oldName, moved.Sha256, CancellationToken.None);
                    _files = _files.Select(file => file.Root == "stickers" && file.RelativePath == newName
                        ? new RemoteFile
                        {
                            Root = "stickers", RelativePath = oldName, Kind = "image", Size = currentImage.Size,
                            Sha256 = currentImage.Sha256, ModifiedUtc = currentImage.ModifiedUtc, Editable = false
                        }
                        : file).ToList();
                }
                catch (Exception rollbackError)
                {
                    _setConnected(false);
                    throw new InvalidOperationException("目录保存失败且图片名自动回滚失败，请重新连接扫描。保存错误：" +
                        saveError.Message + "；回滚错误：" + rollbackError.Message, rollbackError);
                }
                RestoreCatalog();
                throw new InvalidOperationException("目录保存失败，图片文件名已自动恢复。原因：" + saveError.Message, saveError);
            }

            var count = _files.Count(x => x.Root == "stickers" && x.IsImage);
            return new
            {
                oldFileName = oldName,
                fileName = newName,
                size = moved.Size,
                sha256 = moved.Sha256,
                stickerCount = count,
                stickerFiles = GetUiRows(),
                stickerEditingEnabled = _catalog is not null,
                catalogText = _catalogContent?.Text,
                manifestText = _manifestContent?.Text
            };
        }
        catch
        {
            if (_catalogContent?.Text == oldCatalogText) RestoreCatalog();
            throw;
        }
        finally { _setBusy(false); }
    }

    public StickerUploadBeginResult BeginUpload(JsonElement payload)
    {
        EnsureConnected();
        var fileName = payload.GetProperty("fileName").GetString() ?? "";
        var size = payload.GetProperty("size").GetInt64();
        var result = _uploads.Begin(fileName, size, _files, _privateDirectory());
        _setBusy(true);
        return result;
    }

    public StickerUploadAppendResult AppendUpload(JsonElement payload)
    {
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        var offset = payload.GetProperty("offset").GetInt64();
        var contentBase64 = payload.GetProperty("contentBase64").GetString() ?? "";
        var sha256 = payload.GetProperty("sha256").GetString() ?? "";
        return _uploads.Append(uploadId, offset, contentBase64, sha256);
    }

    public async Task<object> CommitUploadAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        using var session = _uploads.TakeForCommit(uploadId);
        try
        {
        if (session.Content.Length != session.Size) throw new InvalidDataException("上传内容长度不完整，请重新上传。");
        session.Content.Flush(flushToDisk: true);
        session.Content.Position = 0;
        var header = new byte[Math.Min(16 * 1024, checked((int)session.Size))];
        var headerBytes = await session.Content.ReadAsync(header.AsMemory(0, header.Length));
        if (!StickerUploadService.MatchesImage(session.FileName, header.AsSpan(0, headerBytes).ToArray()))
            throw new InvalidDataException("文件内容与扩展名不匹配，或图片格式不受支持。");
        session.Content.Position = 0;
        EnsureCatalogLoaded();

        var existingRows = _catalog!.Rows.ToList();
        if (!_catalog.TryAddImage(session.FileName, out var addedRow, out var addError))
            throw new InvalidDataException(addError);
        var updatedCatalog = _catalog.SerializeWithEdits();
        if (!StickerManifestSynchronizer.TryAppendRow(_manifestContent!.Text!, existingRows, addedRow,
            out var appendedManifest, out var appendError))
        {
            RestoreCatalog();
            throw new InvalidDataException("图片尚未上传：" + appendError);
        }
        var updatedRows = _catalog.Rows.ToList();
        if (!StickerManifestSynchronizer.TryUpdate(appendedManifest, updatedRows,
            out var updatedManifest, out var verifyError))
        {
            RestoreCatalog();
            throw new InvalidDataException("图片尚未上传，无法校验自动登记：" + verifyError);
        }

            RemoteStickerUploadResult uploaded;
            try { uploaded = await _stickersRemote.UploadStickerAsync(_connection(), session.FileName, session.Content, session.Size, cancellationToken); }
            catch { RestoreCatalog(); throw; }

            _files = _files.Append(new RemoteFile
            {
                Root = "stickers", RelativePath = uploaded.RelativePath, Kind = "image", Size = uploaded.Size,
                Sha256 = uploaded.Sha256, ModifiedUtc = DateTimeOffset.UtcNow, Editable = false
            }).ToList();

            try { await SavePairAsync(updatedCatalog, updatedManifest, cancellationToken); }
            catch (Exception registrationError)
            {
                RestoreCatalog();
                return new
                {
                    fileName = uploaded.RelativePath,
                    size = uploaded.Size,
                    sha256 = uploaded.Sha256,
                    stickerCount = _files.Count(x => x.Root == "stickers" && x.IsImage),
                    stickerFiles = GetUiRows(),
                    stickerEditingEnabled = _catalog is not null,
                    registered = false,
                    registrationError = registrationError.Message,
                    catalogText = _catalogContent?.Text,
                    manifestText = _manifestContent?.Text
                };
            }

            return new
            {
                fileName = uploaded.RelativePath,
                size = uploaded.Size,
                sha256 = uploaded.Sha256,
                stickerCount = _files.Count(x => x.Root == "stickers" && x.IsImage),
                stickerFiles = GetUiRows(),
                stickerEditingEnabled = _catalog is not null,
                registered = true,
                registrationError = "",
                catalogText = _catalogContent?.Text,
                manifestText = _manifestContent?.Text
            };
        }
        finally { _setBusy(false); }
    }

    public void CancelUpload(JsonElement payload)
    {
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        _uploads.Cancel(uploadId);
        _setBusy(false);
    }

    public List<StickerUiRow> GetUiRows()
    {
        var rows = (_catalog?.Rows ?? []).Select(row => new StickerUiRow(row.Id, row.ImagePath, row.TagsText, row.Weight, true)).ToList();
        var known = new HashSet<string>(rows.Select(x => x.ImagePath), StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files.Where(x => x.Root == "stickers" && x.IsImage))
        {
            if (known.Add(file.RelativePath))
                rows.Add(new StickerUiRow(Path.GetFileNameWithoutExtension(file.RelativePath), file.RelativePath, "", 1, false));
        }
        return rows;
    }

    public void Dispose()
    {
        _uploads.Dispose();
        _setBusy(false);
    }

    private RemoteFile FindImage(string path)
    {
        return _files.FirstOrDefault(x => x.Root == "stickers" && x.IsImage &&
            x.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("图片不在本次扫描的表情包目录内。");
    }

    private void EnsureConnected()
    {
        if (!_isConnected()) throw new InvalidOperationException("请先连接服务器并扫描文件。");
    }

    private void EnsureCatalogLoaded()
    {
        if (_catalog is null || _catalogContent?.Text is null || _manifestContent?.Text is null)
            throw new InvalidOperationException("当前标签目录格式无法安全编辑，请先检查 catalog.json 与 MANIFEST.md。");
    }

    private void RestoreCatalog()
    {
        if (_catalogContent?.Text is null)
        {
            _catalog = null;
            return;
        }
        StickerCatalogEditor.TryParse(_catalogContent.Text,
            _files.Where(x => x.Root == "stickers" && x.IsImage).ToList(), out _catalog, out _);
    }

    private static void ValidateWeight(double weight)
    {
        if (!double.IsFinite(weight) || weight < 0 || weight > 1_000_000)
            throw new InvalidDataException("表情包权重必须是 0 到 1,000,000 之间的有限数字。");
    }

    private sealed record StickerEditRow(string Id, string ImagePath, string TagsText, double? Weight = null);
}
