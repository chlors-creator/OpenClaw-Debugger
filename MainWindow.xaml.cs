using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace OpenClawDebugger;

public partial class MainWindow : Window
{
    private readonly RemoteOpenClawClient _remote = new();
    private UserSettings _settings = new();
    private LocalSnapshotStore? _snapshots;
    private IReadOnlyList<RemoteFile> _files = [];
    private RemoteFileContent? _currentMemory;
    private string _memoryOriginalText = "";
    private bool _memoryEditing;
    private bool _updatingMemoryText;
    private RemoteFile? _catalogFile;
    private RemoteFile? _manifestFile;
    private RemoteFileContent? _catalogContent;
    private RemoteFileContent? _manifestContent;
    private ParsedStickerCatalog? _catalog;
    private Dictionary<string, string> _originalStickerTags = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewCancellation;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await SettingsRepository.LoadAsync();
            _snapshots = new LocalSnapshotStore(_settings.PrivateDirectory);
            ShowSettings();
            SnapshotPathText.Text = Path.Combine(_settings.PrivateDirectory, "Rollback");
            RootPathsText.Text = $"工作区：{_settings.Connection.WorkspacePath}    ·    表情包：{_settings.Connection.StickersPath}";
            SetStatus($"私密数据目录已就绪：{_settings.PrivateDirectory}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ShowSettings()
    {
        HostBox.Text = _settings.Connection.Host;
        UsernameBox.Text = _settings.Connection.Username;
        PortBox.Text = _settings.Connection.Port.ToString();
        WorkspaceBox.Text = _settings.Connection.WorkspacePath;
        StickersBox.Text = _settings.Connection.StickersPath;
        PrivatePathBox.Text = _settings.PrivateDirectory;
    }

    private bool ReadSettingsFromUi()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "SSH 端口需要是 1–65535 之间的数字。", "设置错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.Connection.Host = HostBox.Text.Trim();
        _settings.Connection.Username = UsernameBox.Text.Trim();
        _settings.Connection.Port = port;
        _settings.Connection.WorkspacePath = WorkspaceBox.Text.Trim();
        _settings.Connection.StickersPath = StickersBox.Text.Trim();
        _settings.PrivateDirectory = SettingsRepository.DefaultPrivateDirectory;
        return true;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettingsFromUi()) return;
        try
        {
            await SettingsRepository.SaveAsync(_settings);
            _snapshots = new LocalSnapshotStore(_settings.PrivateDirectory);
            RootPathsText.Text = $"工作区：{_settings.Connection.WorkspacePath}    ·    表情包：{_settings.Connection.StickersPath}";
            SnapshotPathText.Text = Path.Combine(_settings.PrivateDirectory, "Rollback");
            SetStatus("设置已保存到仓库外的私密目录。");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenPrivateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.PrivateDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { _settings.PrivateDirectory },
                UseShellExecute = false
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAndScanAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ConnectAndScanAsync();

    private async Task ConnectAndScanAsync()
    {
        if (!ReadSettingsFromUi()) return;
        try { StickerGrid.CommitEdit(DataGridEditingUnit.Cell, true); StickerGrid.CommitEdit(DataGridEditingUnit.Row, true); }
        catch { }
        if (_memoryEditing || HasStickerDraft())
        {
            MessageBox.Show(this, "请先保存或放弃未完成的记忆或标签修改，再重新扫描服务器。", "存在未完成的编辑",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { await SettingsRepository.SaveAsync(_settings); }
        catch (Exception ex) { ShowError(ex); return; }
        _snapshots ??= new LocalSnapshotStore(_settings.PrivateDirectory);
        SetBusy(true);
        ConnectionStatusText.Text = "正在连接";
        try
        {
            _files = await _remote.ConnectAndListAsync(_settings.Connection);
            var memoryFiles = _files
                .Where(x => x.Root == "workspace" && !x.IsImage && x.Editable)
                .OrderBy(x => x.RelativePath.StartsWith("memory/", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            MemoryFilesList.ItemsSource = memoryFiles;
            MemoryCountText.Text = memoryFiles.Count.ToString();
            var imageFiles = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
            StickerCountText.Text = imageFiles.Count.ToString();
            ConnectionStatusText.Text = $"已连接 · {memoryFiles.Count} 个文档 · {imageFiles.Count} 张图片";
            RefreshButton.IsEnabled = true;
            RootPathsText.Text = $"工作区：{_settings.Connection.WorkspacePath}    ·    表情包：{_settings.Connection.StickersPath}";
            SetStatus("SSH 连接成功；已扫描允许管理的工作区 Markdown 与表情包目录。");
            await LoadStickerCatalogAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = "连接失败";
            RefreshButton.IsEnabled = false;
            SetStatus(ex.Message, isError: true);
        }
        finally { SetBusy(false); }
    }

    private async Task LoadStickerCatalogAsync()
    {
        _catalogFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "catalog.json");
        _manifestFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "MANIFEST.md");
        _catalog = null;
        _catalogContent = null;
        _manifestContent = null;
        SaveStickerButton.IsEnabled = false;
        StickerPreviewImage.Source = null;

        if (_catalogFile is null || _manifestFile is null)
        {
            StickerGrid.ItemsSource = _files.Where(x => x.Root == "stickers" && x.IsImage)
                .Select(x => new StickerRow { Id = Path.GetFileNameWithoutExtension(x.RelativePath), ImagePath = x.RelativePath })
                .ToList();
            StickerStatusText.Text = "找不到 catalog.json 或 MANIFEST.md；标签编辑已关闭以避免不完整写入。";
            return;
        }

        try
        {
            var catalogTask = _remote.ReadAsync(_settings.Connection, _catalogFile);
            var manifestTask = _remote.ReadAsync(_settings.Connection, _manifestFile);
            await Task.WhenAll(catalogTask, manifestTask);
            _catalogContent = catalogTask.Result;
            _manifestContent = manifestTask.Result;
            var images = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
            if (!StickerCatalogEditor.TryParse(_catalogContent.Text ?? "", images, out _catalog, out var error))
            {
                StickerGrid.ItemsSource = images.Select(x =>
                    new StickerRow { Id = Path.GetFileNameWithoutExtension(x.RelativePath), ImagePath = x.RelativePath }).ToList();
                StickerStatusText.Text = error;
                return;
            }

            StickerGrid.ItemsSource = _catalog!.Rows;
            StickerGrid.IsReadOnly = false;
            SaveStickerButton.IsEnabled = true;
            StickerStatusText.Text = $"已读取 {_catalog.Rows.Count} 条目录记录。标签保存会同时更新 catalog.json 和 MANIFEST.md，并在写入前生成加密快照。";
            StickerPreviewTitle.Text = "选择一张贴图";
        }
        catch (Exception ex)
        {
            StickerStatusText.Text = $"读取目录失败：{ex.Message}";
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void MemoryFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_memoryEditing || MemoryFilesList.SelectedItem is not RemoteFile file) return;
        SetBusy(true);
        MemoryEditor.Text = "";
        MemoryFileTitle.Text = file.RelativePath;
        MemoryFileInfo.Text = "正在读取服务器文件…";
        EditMemoryButton.IsEnabled = false;
        try
        {
            _currentMemory = await _remote.ReadAsync(_settings.Connection, file);
            _memoryOriginalText = _currentMemory.Text ?? "";
            _updatingMemoryText = true;
            MemoryEditor.Text = _memoryOriginalText;
            _updatingMemoryText = false;
            MemoryFileInfo.Text = $"{_currentMemory.Size:N0} 字节 · {_currentMemory.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · SHA-256 {_currentMemory.Sha256}";
            EditMemoryButton.IsEnabled = file.Editable;
            SetStatus($"已读取 {file.RelativePath}；内容仅保留在当前应用内存中。");
        }
        catch (Exception ex)
        {
            _updatingMemoryText = false;
            MemoryFileInfo.Text = "读取失败。";
            SetStatus(ex.Message, isError: true);
        }
        finally { SetBusy(false); }
    }

    private void EditMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMemory is null) return;
        _memoryEditing = true;
        MemoryEditor.IsReadOnly = false;
        MemoryFilesList.IsEnabled = false;
        EditMemoryButton.IsEnabled = false;
        CancelMemoryButton.IsEnabled = true;
        SaveMemoryButton.IsEnabled = false;
        SetStatus("编辑模式已开启；保存时将显示差异并检查远程冲突。");
        MemoryEditor.Focus();
    }

    private void CancelMemory_Click(object sender, RoutedEventArgs e)
    {
        _updatingMemoryText = true;
        MemoryEditor.Text = _memoryOriginalText;
        _updatingMemoryText = false;
        EndMemoryEditing();
        SetStatus("已放弃未保存的修改。");
    }

    private void MemoryEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingMemoryText || !_memoryEditing) return;
        SaveMemoryButton.IsEnabled = !string.Equals(MemoryEditor.Text, _memoryOriginalText, StringComparison.Ordinal);
    }

    private async void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMemory is null || _snapshots is null) return;
        var newText = MemoryEditor.Text;
        if (string.Equals(newText, _memoryOriginalText, StringComparison.Ordinal))
        {
            EndMemoryEditing();
            return;
        }

        var review = new DiffReviewWindow(
            $"保存 {Path.GetFileName(_currentMemory.RelativePath)}",
            _memoryOriginalText, newText) { Owner = this };
        if (review.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            var file = _files.First(x => x.Root == _currentMemory.Root && x.RelativePath == _currentMemory.RelativePath);
            var latest = await _remote.ReadAsync(_settings.Connection, file);
            if (!string.Equals(latest.Sha256, _currentMemory.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RemoteConflictException("服务器文件在加载后发生了变化。没有覆盖它；请重新选择文件并合并修改。");

            var snapshot = await _snapshots.SaveAsync(
                latest.Root, latest.RelativePath, latest.RawBytes, latest.Sha256);
            var newHash = await _remote.WriteAsync(
                _settings.Connection, file, newText, latest.Sha256);
            var newBytes = new UTF8Encoding(false).GetBytes(newText);
            _currentMemory = latest with
            {
                Sha256 = newHash,
                Size = newBytes.Length,
                ModifiedUtc = DateTimeOffset.UtcNow,
                Text = newText,
                RawBytes = newBytes
            };
            _memoryOriginalText = newText;
            _updatingMemoryText = true;
            MemoryEditor.Text = newText;
            _updatingMemoryText = false;
            MemoryFileInfo.Text = $"{newBytes.Length:N0} 字节 · 已保存 · SHA-256 {newHash}";
            EndMemoryEditing();
            SetStatus($"保存完成。原版本已 DPAPI 加密快照：{snapshot.Id}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            ShowError(ex);
        }
        finally { SetBusy(false); }
    }

    private void EndMemoryEditing()
    {
        _memoryEditing = false;
        MemoryEditor.IsReadOnly = true;
        MemoryFilesList.IsEnabled = true;
        CancelMemoryButton.IsEnabled = false;
        SaveMemoryButton.IsEnabled = false;
        EditMemoryButton.IsEnabled = _currentMemory is not null;
    }

    private async void StickerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StickerGrid.SelectedItem is not StickerRow row) return;
        StickerPreviewTitle.Text = row.ImagePath;
        StickerPreviewTags.Text = string.IsNullOrWhiteSpace(row.TagsText) ? "当前没有标签" : $"标签：{row.TagsText}";
        var file = _files.FirstOrDefault(x => x.Root == "stickers" && x.IsImage &&
            string.Equals(x.RelativePath, row.ImagePath, StringComparison.OrdinalIgnoreCase));
        if (file is null) return;

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;
        try
        {
            var content = await _remote.ReadAsync(_settings.Connection, file, token);
            if (token.IsCancellationRequested) return;
            if (content.Binary is null) return;
            using var stream = new MemoryStream(content.Binary);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            StickerPreviewImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StickerStatusText.Text = $"图片读取失败：{ex.Message}"; }
    }

    private async void SaveSticker_Click(object sender, RoutedEventArgs e)
    {
        StickerGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StickerGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_catalog is null || _catalogContent?.Text is null || _manifestContent?.Text is null) return;

        string updatedCatalog;
        string updatedManifest;
        try
        {
            updatedCatalog = _catalog.SerializeWithEdits();
            using var parsedJson = JsonDocument.Parse(updatedCatalog);
            if (!StickerManifestSynchronizer.TryUpdate(
                    _manifestContent.Text, _catalog.Rows, out updatedManifest, out var error))
            {
                StickerStatusText.Text = error;
                MessageBox.Show(this, error, "无法安全同步标签", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }
        await SaveStickerPairAsync(updatedCatalog, updatedManifest);
    }

    private async Task<bool> SaveStickerPairAsync(string updatedCatalog, string updatedManifest)
    {
        if (_catalogFile is null || _manifestFile is null || _catalogContent is null ||
            _manifestContent is null || _snapshots is null) return false;
        var oldCatalog = _catalogContent;
        var oldManifest = _manifestContent;
        if (updatedCatalog == oldCatalog.Text && updatedManifest == oldManifest.Text)
        {
            SetStatus("标签没有变化。");
            return true;
        }

        var before = $"catalog.json\n{oldCatalog.Text}\n\n----- MANIFEST.md -----\n{oldManifest.Text}";
        var after = $"catalog.json\n{updatedCatalog}\n\n----- MANIFEST.md -----\n{updatedManifest}";
        var review = new DiffReviewWindow("保存表情包标签（两份文件）", before, after) { Owner = this };
        if (review.ShowDialog() != true) return false;

        SetBusy(true);
        string? catalogNewHash = null;
        try
        {
            var latestCatalogTask = _remote.ReadAsync(_settings.Connection, _catalogFile);
            var latestManifestTask = _remote.ReadAsync(_settings.Connection, _manifestFile);
            await Task.WhenAll(latestCatalogTask, latestManifestTask);
            var latestCatalog = latestCatalogTask.Result;
            var latestManifest = latestManifestTask.Result;
            if (latestCatalog.Sha256 != oldCatalog.Sha256 || latestManifest.Sha256 != oldManifest.Sha256)
                throw new RemoteConflictException("服务器上的目录文件已发生变化。没有覆盖；请重新扫描后再编辑。");

            var snapshots = new List<SnapshotInfo>();
            if (updatedCatalog != oldCatalog.Text)
                snapshots.Add(await _snapshots.SaveAsync("stickers", "catalog.json", oldCatalog.RawBytes, oldCatalog.Sha256));
            if (updatedManifest != oldManifest.Text)
                snapshots.Add(await _snapshots.SaveAsync("stickers", "MANIFEST.md", oldManifest.RawBytes, oldManifest.Sha256));

            if (updatedCatalog != oldCatalog.Text)
                catalogNewHash = await _remote.WriteAsync(_settings.Connection, _catalogFile, updatedCatalog, oldCatalog.Sha256);

            string manifestNewHash = oldManifest.Sha256;
            try
            {
                if (updatedManifest != oldManifest.Text)
                    manifestNewHash = await _remote.WriteAsync(_settings.Connection, _manifestFile, updatedManifest, oldManifest.Sha256);
            }
            catch (Exception writeError)
            {
                if (catalogNewHash is not null)
                {
                    try
                    {
                        var currentCatalog = await _remote.ReadAsync(_settings.Connection, _catalogFile);
                        if (currentCatalog.Sha256 == catalogNewHash)
                            await _remote.WriteBytesAsync(_settings.Connection, _catalogFile,
                                oldCatalog.RawBytes, catalogNewHash);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(
                            $"MANIFEST.md 写入失败，catalog.json 自动回滚也失败。请保留本机快照并检查服务器。原因：{rollbackError.Message}",
                            writeError);
                    }
                }
                throw;
            }

            var catalogBytes = new UTF8Encoding(false).GetBytes(updatedCatalog);
            var manifestBytes = new UTF8Encoding(false).GetBytes(updatedManifest);
            _catalogContent = oldCatalog with
            {
                Text = updatedCatalog, RawBytes = catalogBytes, Sha256 = catalogNewHash ?? oldCatalog.Sha256,
                Size = catalogBytes.Length, ModifiedUtc = DateTimeOffset.UtcNow
            };
            _manifestContent = oldManifest with
            {
                Text = updatedManifest, RawBytes = manifestBytes, Sha256 = manifestNewHash,
                Size = manifestBytes.Length, ModifiedUtc = DateTimeOffset.UtcNow
            };
            var imageFiles = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
            if (StickerCatalogEditor.TryParse(updatedCatalog, imageFiles, out _catalog, out var parseError))
            {
                StickerGrid.ItemsSource = _catalog!.Rows;
                _originalStickerTags = _catalog.Rows.ToDictionary(TagKey, x => x.TagsText, StringComparer.OrdinalIgnoreCase);
                StickerGrid.IsReadOnly = false;
                SaveStickerButton.IsEnabled = true;
            }
            else
            {
                StickerStatusText.Text = parseError;
            }

            SetStatus($"表情包标签保存完成；已生成 {snapshots.Count} 份加密快照。");
            StickerStatusText.Text = "标签清单已同步写入 catalog.json 与 MANIFEST.md。";
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            ShowError(ex);
            return false;
        }
        finally { SetBusy(false); }
    }

    private async void OpenRawStickerFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogContent?.Text is null || _manifestContent?.Text is null)
        {
            MessageBox.Show(this, "请先连接服务器并读取标签目录。", "尚未连接",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rawOriginalCatalog = _catalogContent.Text;
        var rawOriginalManifest = _manifestContent.Text;
        var rawApplied = false;
        var window = new Window
        {
            Title = "高级编辑：catalog.json 与 MANIFEST.md",
            Width = 1120,
            Height = 760,
            MinWidth = 850,
            MinHeight = 600,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var catalogBox = new TextBox
        {
            Text = _catalogContent.Text, AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
            FontSize = 12, TextWrapping = TextWrapping.NoWrap
        };
        var manifestBox = new TextBox
        {
            Text = _manifestContent.Text, AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
            FontSize = 12, TextWrapping = TextWrapping.NoWrap
        };
        var catalogPanel = new DockPanel();
        var catalogTitle = new TextBlock { Text = "catalog.json", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Foreground = System.Windows.Media.Brushes.Black };
        DockPanel.SetDock(catalogTitle, Dock.Top);
        catalogPanel.Children.Add(catalogTitle);
        catalogPanel.Children.Add(catalogBox);
        var manifestPanel = new DockPanel();
        var manifestTitle = new TextBlock { Text = "MANIFEST.md", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Foreground = System.Windows.Media.Brushes.Black };
        DockPanel.SetDock(manifestTitle, Dock.Top);
        manifestPanel.Children.Add(manifestTitle);
        manifestPanel.Children.Add(manifestBox);
        Grid.SetColumn(manifestPanel, 2);
        columns.Children.Add(catalogPanel);
        columns.Children.Add(manifestPanel);
        Grid.SetRow(columns, 0);
        grid.Children.Add(columns);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "关闭", Padding = new Thickness(16, 8, 16, 8) };
        close.Click += (_, _) => window.Close();
        var save = new Button { Content = "校验并保存两份文件", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
        save.Click += async (_, _) =>
        {
            try
            {
                using var parsed = JsonDocument.Parse(catalogBox.Text);
                if (await SaveStickerPairAsync(catalogBox.Text, manifestBox.Text))
                {
                    rawApplied = true;
                    window.Close();
                }
            }
            catch (JsonException ex)
            {
                MessageBox.Show(window, $"catalog.json 格式错误：{ex.Message}", "JSON 校验失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { ShowError(ex); }
        };
        window.Closing += (_, args) =>
        {
            if (rawApplied ||
                (catalogBox.Text == rawOriginalCatalog && manifestBox.Text == rawOriginalManifest)) return;
            var answer = MessageBox.Show(window, "原始标签文件有未保存的修改。确定放弃吗？",
                "未保存的标签文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) args.Cancel = true;
        };        buttons.Children.Add(close);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        window.Content = grid;
        window.ShowDialog();
    }

    private static string TagKey(StickerRow row) => row.Id + "\0" + row.ImagePath;

    private bool HasStickerDraft()
    {
        if (_catalog is null || _originalStickerTags.Count == 0) return false;
        return _catalog.Rows.Any(row =>
            !_originalStickerTags.TryGetValue(TagKey(row), out var original) ||
            !string.Equals(original, row.TagsText, StringComparison.Ordinal));
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var hasMemoryDraft = _memoryEditing &&
            !string.Equals(MemoryEditor.Text, _memoryOriginalText, StringComparison.Ordinal);
        try { StickerGrid.CommitEdit(DataGridEditingUnit.Cell, true); StickerGrid.CommitEdit(DataGridEditingUnit.Row, true); }
        catch { }
        var hasStickerDraft = HasStickerDraft();
        if (!hasMemoryDraft && !hasStickerDraft) return;
        var result = MessageBox.Show(this,
            "还有未保存的记忆或标签修改。确定放弃并关闭吗？",
            "存在未保存内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) e.Cancel = true;
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy && _files.Count > 0;
        SaveMemoryButton.IsEnabled = !busy && _memoryEditing &&
            !string.Equals(MemoryEditor.Text, _memoryOriginalText, StringComparison.Ordinal);
        SaveStickerButton.IsEnabled = !busy && _catalog is not null && _catalogFile is not null && _manifestFile is not null;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusLine.Text = message;
        StatusLine.Foreground = isError
            ? System.Windows.Media.Brushes.Salmon
            : System.Windows.Media.Brushes.LightSteelBlue;
        ConnectionStatusText.ToolTip = message;
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
}

internal sealed class DiffReviewWindow : Window
{
    public DiffReviewWindow(string title, string before, string after)
    {
        Title = title;
        Width = 1120;
        Height = 760;
        MinWidth = 780;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var description = new TextBlock
        {
            Text = "请先比较保存前和保存后的完整内容。只有确认后才会写入服务器。",
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = System.Windows.Media.Brushes.Black
        };
        root.Children.Add(description);

        var panels = new Grid();
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        panels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        var left = CreatePanel("保存前", before);
        var right = CreatePanel("保存后", after);
        Grid.SetColumn(right, 2);
        panels.Children.Add(left);
        panels.Children.Add(right);
        Grid.SetRow(panels, 1);
        root.Children.Add(panels);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "返回编辑", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var apply = new Button { Content = "确认保存", Padding = new Thickness(16, 8, 16, 8), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White };
        apply.Click += (_, _) => { DialogResult = true; Close(); };
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Content = root;
    }

    private static FrameworkElement CreatePanel(string title, string value)
    {
        var panel = new DockPanel();
        var header = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7),
            Foreground = System.Windows.Media.Brushes.Black
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        var text = new TextBox
        {
            Text = ClipText(value),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
            FontSize = 12
        };
        panel.Children.Add(text);
        return panel;
    }

    private static string ClipText(string value) =>
        value.Length > 150_000 ? value[..150_000] + Environment.NewLine + "[预览已截断]" : value;
}