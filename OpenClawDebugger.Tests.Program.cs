using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClawDebugger;

var suite = new TestSuite();
await suite.RunAsync();

sealed class TestSuite
{
    private int _passed;
    private int _failed;

    public async Task RunAsync()
    {
        await RunAsync("备份暂停继续", BackupPauseAndResumeAsync);
        await RunAsync("备份取消", BackupCancellationAsync);
        await RunAsync("网络中断重试与断点", BackupRetryAndResumeAsync);
        await RunAsync("上传校验和重复分块", UploadChecksumAndDuplicateAsync);
        await RunAsync("标签事务前置回滚", StickerTransactionPreflightAsync);
        await RunAsync("桥接协议参数校验", BridgePayloadValidationAsync);
        await RunAsync("旧请求状态保护", StaleRequestGuardAsync);
        await RunAsync("结构化日志脱敏", StructuredLogAsync);
        await RunAsync("模型拖拽自动保存", ModelAutoSaveContractAsync);
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项。" );
        if (_failed > 0) Environment.ExitCode = 1;
    }

    private async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static async Task BackupPauseAndResumeAsync()
    {
        var pause = new BackupPauseController();
        Assert(pause.Pause(), "暂停应改变状态");
        var waiting = pause.WaitIfPausedAsync();
        await Task.Delay(30);
        Assert(!waiting.IsCompleted, "暂停期间任务应等待");
        Assert(pause.Resume(), "继续应改变状态");
        await waiting;
    }

    private static async Task BackupCancellationAsync()
    {
        var root = NewTempDirectory();
        var remote = new BlockingSnapshotClient();
        var coordinator = new BackupCoordinator(remote, root, () => false, _ => { }, (_, _) => { });
        var task = coordinator.StartAsync(new ConnectionSettings());
        await Task.Delay(50);
        await AssertThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(new ConnectionSettings()));
        coordinator.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => task);
        coordinator.Dispose(); Delete(root);
    }

    private static async Task BackupRetryAndResumeAsync()
    {
        var root = NewTempDirectory();
        var old = Path.Combine(root, "old-backup");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "server-rootfs.tar.gz"), [1]);
        File.WriteAllText(Path.Combine(old, "snapshot-manifest.json"), "{}");
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var remote = new RetryingSnapshotClient(RetryingSnapshotClient.CreateGzipPayload("OpenClaw snapshot test"));
        var result = await new LocalServerBackupStore(root).CreateAsync(new ConnectionSettings(), remote,
            cancellationToken: CancellationToken.None, retentionCount: 1);
        Assert(File.Exists(result.ArchivePath), "归档应原子提交");
        Assert(!Directory.EnumerateDirectories(root, ".staging-*", SearchOption.TopDirectoryOnly).Any(), "不应残留临时目录");
        Assert(remote.TransferCalls >= 2, "应发生一次断点重试");
        Assert(!Directory.Exists(old), "应自动清理超出保留数量的旧备份");
        Delete(root);
    }

    private static Task UploadChecksumAndDuplicateAsync()
    {
        var root = NewTempDirectory();
        var service = new StickerUploadService(root);
        var begin = service.Begin("test.gif", 4, [], root);
        var bytes = Encoding.ASCII.GetBytes("GIF8");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var first = service.Append(begin.UploadId, 0, Convert.ToBase64String(bytes), hash);
        var duplicate = service.Append(begin.UploadId, 0, Convert.ToBase64String(bytes), hash);
        Assert(first.ReceivedBytes == 4 && duplicate.ReceivedBytes == 4, "重复分块应幂等返回");
        AssertThrows<InvalidDataException>(() => service.Append(begin.UploadId, 0, Convert.ToBase64String(bytes), "bad"));
        service.Cancel(begin.UploadId); service.Dispose(); Delete(root);
        return Task.CompletedTask;
    }

    private static Task StickerTransactionPreflightAsync()
    {
        var original = "1 sticker.gif 标签：旧标签";
        var rows = new[] { new StickerRow { Id = "2", ImagePath = "other.gif", TagsText = "新" } };
        Assert(!StickerManifestSynchronizer.TryUpdate(original, rows, out var updated, out _), "不匹配的标签文件应拒绝写入");
        Assert(updated == original, "拒绝写入时原文应保持不变");
        var sourceRoot = FindSourceRoot();
        var remoteProgram = File.ReadAllText(Path.Combine(sourceRoot, "RemoteAgentProgram.cs"));
        Assert(remoteProgram.Contains("os.replace(restore_path, manifest_path)", StringComparison.Ordinal), "远程事务应包含 manifest 回滚路径");
        return Task.CompletedTask;
    }

    private static Task BridgePayloadValidationAsync()
    {
        var contract = new BridgeCommandContract
        {
            ProtocolVersion = BridgeProtocol.Version,
            Commands = new Dictionary<string, BridgeCommandDefinition>
            {
                ["save"] = new() { Payload = new Dictionary<string, string> { ["path"] = "string", ["size"] = "integer" } }
            }
        };
        using var valid = JsonDocument.Parse("{\"path\":\"a\",\"size\":3}");
        contract.ValidatePayload("save", valid.RootElement);
        using var invalid = JsonDocument.Parse("{\"path\":3,\"size\":\"3\"}");
        AssertThrows<InvalidDataException>(() => contract.ValidatePayload("save", invalid.RootElement));
        var actual = BridgeCommandContract.Load(Path.Combine(FindSourceRoot(), "WebUi", "bridge-contract.json"));
        using var missing = JsonDocument.Parse("{\"host\":\"example\"}");
        AssertThrows<InvalidDataException>(() => actual.ValidatePayload("saveSettings", missing.RootElement));
        return Task.CompletedTask;
    }

    private static Task StaleRequestGuardAsync()
    {
        var sourceRoot = FindSourceRoot();
        var app = File.ReadAllText(Path.Combine(sourceRoot, "WebUi", "app.js"));
        var sticker = File.ReadAllText(Path.Combine(sourceRoot, "WebUi", "sticker-controller.js"));
        Assert(app.Contains("connectGeneration", StringComparison.Ordinal), "连接应有代际编号");
        Assert(sticker.Contains("previewGeneration", StringComparison.Ordinal), "预览应有代际编号");
        Assert(sticker.Contains("AbortController", StringComparison.Ordinal), "预览应支持取消");
        return Task.CompletedTask;
    }

    private static Task StructuredLogAsync()
    {
        var root = NewTempDirectory();
        var store = OperationLogStore.Configure(root);
        using (var scope = store.Begin("op-1", "saveMemory"))
        {
            store.Stage("ssh-failure", "password=secret C:\\Users\\private\\file.md", 2);
            scope.Complete("failed", false, "连接失败");
        }
        var export = store.Export();
        var text = File.ReadAllText(export);
        Assert(text.Contains("operationId", StringComparison.Ordinal), "日志应包含操作 ID");
        Assert(!text.Contains("secret", StringComparison.OrdinalIgnoreCase), "日志不应暴露敏感参数");
        Assert(!text.Contains("C:\\Users\\private", StringComparison.OrdinalIgnoreCase), "日志不应暴露本地路径");
        Delete(root);
        return Task.CompletedTask;
    }

    private static Task ModelAutoSaveContractAsync()
    {
        var sourceRoot = FindSourceRoot();
        var contract = BridgeCommandContract.Load(Path.Combine(sourceRoot, "WebUi", "bridge-contract.json"));
        Assert(contract.Commands.ContainsKey("getModels"), "协议应包含模型读取命令");
        Assert(contract.Commands.ContainsKey("setModelOrder"), "协议应包含模型顺序命令");
        Assert(contract.Commands.ContainsKey("testModelLatency"), "协议应包含延迟测试命令");
        Assert(contract.Commands.ContainsKey("addModel"), "协议应包含添加模型命令");
        var html = File.ReadAllText(Path.Combine(sourceRoot, "WebUi", "index.html"));
        var controller = File.ReadAllText(Path.Combine(sourceRoot, "WebUi", "model-controller.js"));
        Assert(html.IndexOf("data-tab=\"models\"", StringComparison.Ordinal) < html.IndexOf("data-tab=\"stickers\"", StringComparison.Ordinal), "模型导航应位于表情包之前");
        Assert(controller.Contains("bridgeCall('setModelOrder'", StringComparison.Ordinal), "拖拽后应直接调用自动保存命令");
        Assert(!html.Contains("saveModelOrderButton", StringComparison.Ordinal), "模型页不应有保存顺序按钮");
        Assert(File.ReadAllText(Path.Combine(sourceRoot, "RemoteAgentProgram.cs")).Contains("models_set_order", StringComparison.Ordinal), "服务器代理应实现模型顺序写入");
        return Task.CompletedTask;
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-debugger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static string FindSourceRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
        };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "RemoteAgentProgram.cs")) &&
            File.Exists(Path.Combine(path, "WebUi", "app.js")))
            ?? throw new DirectoryNotFoundException("找不到 OpenClaw Debugger 源码目录。");
    }

    private static void Delete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("应抛出 " + typeof(T).Name); }
    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("应抛出 " + typeof(T).Name); }
}

sealed class BlockingSnapshotClient : IRemoteSnapshotClient
{
    public Task CreateServerSnapshotAsync(ConnectionSettings settings, string remotePath, BackupPauseController? pauseController = null, CancellationToken cancellationToken = default) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    public Task<long> GetServerSnapshotSizeAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) => Task.FromResult(1L);
    public Task<RemoteSnapshotTransferResult> ResumeServerSnapshotAsync(ConnectionSettings settings, string remotePath, Stream destination, long offset, long totalBytes, BackupPauseController? pauseController = null, IProgress<ServerSnapshotProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromCanceled<RemoteSnapshotTransferResult>(cancellationToken);
    public Task RemoveServerSnapshotAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class RetryingSnapshotClient(byte[] bytes) : IRemoteSnapshotClient
{
    private bool _interrupted;
    public int TransferCalls { get; private set; }
    public Task CreateServerSnapshotAsync(ConnectionSettings settings, string remotePath, BackupPauseController? pauseController = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<long> GetServerSnapshotSizeAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) => Task.FromResult((long)bytes.Length);
    public Task<RemoteSnapshotTransferResult> ResumeServerSnapshotAsync(ConnectionSettings settings, string remotePath, Stream destination, long offset, long totalBytes, BackupPauseController? pauseController = null, IProgress<ServerSnapshotProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        TransferCalls++;
        var remaining = bytes.Length - (int)offset;
        if (!_interrupted)
        {
            _interrupted = true;
            destination.Write(bytes, (int)offset, Math.Min(remaining, 8));
            throw new SnapshotTransferInterruptedException("test network interruption");
        }
        destination.Write(bytes, (int)offset, remaining);
        return Task.FromResult(new RemoteSnapshotTransferResult(bytes.Length, ""));
    }
    public Task RemoveServerSnapshotAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public static byte[] CreateGzipPayload(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8, leaveOpen: true)) writer.Write(value);
        return output.ToArray();
    }
}
