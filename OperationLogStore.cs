using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

/// <summary>
/// 本机结构化操作日志。日志只记录命令、阶段和统计信息，不记录请求正文。
/// </summary>
public sealed class OperationLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly object StaticGate = new();
    private static readonly AsyncLocal<OperationContext?> CurrentContext = new();
    private readonly object _writeGate = new();
    private readonly string _logDirectory;
    private readonly string _exportDirectory;

    private static OperationLogStore? _current;

    private OperationLogStore(string privateDirectory)
    {
        var root = Path.GetFullPath(privateDirectory);
        _logDirectory = Path.Combine(root, "Logs");
        _exportDirectory = Path.Combine(root, "Exports");
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_exportDirectory);
    }

    public static OperationLogStore? Current
    {
        get { lock (StaticGate) return _current; }
    }

    public static OperationLogStore Configure(string privateDirectory)
    {
        lock (StaticGate)
        {
            _current = new OperationLogStore(privateDirectory);
            return _current;
        }
    }

    public OperationLogScope Begin(string operationId, string operation)
    {
        var context = new OperationContext(operationId, operation, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        CurrentContext.Value = context;
        Write(context, "started", null, null, 0, true);
        return new OperationLogScope(this, context);
    }

    public void Stage(string stage, string? message = null, int retryCount = 0)
    {
        var context = CurrentContext.Value;
        if (context is null)
        {
            Write(new OperationContext("system", "system", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
                stage, message, null, retryCount, true);
            return;
        }
        Write(context, stage, message, null, retryCount, true);
    }

    public void SshFailure(string reason, int retryCount = 0) =>
        Stage("ssh-failure", reason, retryCount);

    public string Export()
    {
        Directory.CreateDirectory(_exportDirectory);
        var exportPath = Path.Combine(_exportDirectory,
            $"operation-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        var files = Directory.EnumerateFiles(_logDirectory, "operations-*.jsonl")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var output = new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        foreach (var file in files)
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            input.CopyTo(output);
        }
        output.Flush(flushToDisk: true);
        return exportPath;
    }

    internal void Complete(OperationContext context, string stage, bool success, string? message = null, int retryCount = 0)
    {
        var elapsed = ElapsedMilliseconds(context.StartTimestamp);
        Write(context, stage, message, elapsed, retryCount, success);
        if (ReferenceEquals(CurrentContext.Value, context)) CurrentContext.Value = null;
    }

    private void Write(OperationContext context, string stage, string? message, long? durationMs, int retryCount, bool success)
    {
        var entry = new
        {
            operationId = Redact(context.OperationId),
            operation = Redact(context.Operation),
            stage = Redact(stage),
            startedAt = context.StartedAt,
            timestamp = DateTimeOffset.UtcNow,
            durationMs,
            retryCount,
            success,
            message = Redact(message)
        };
        var path = Path.Combine(_logDirectory, $"operations-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        lock (_writeGate)
        {
            File.AppendAllText(path, line);
        }
    }

    private static long ElapsedMilliseconds(long timestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        return Math.Max(0, (long)Math.Round(elapsed));
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        var text = value;
        text = Regex.Replace(text, @"(?i)(password|passwd|secret|token|private.?key|api.?key|authorization|credential|contentBase64|expectedSha256)\s*[:=]\s*[^\s,;]+", "$1=<redacted>");
        text = Regex.Replace(text, @"[A-Za-z]:\\[^\r\n""']+", "<path>");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])/(?:[^\s""']+/){1,}[^\s""']*", "<path>");
        text = Regex.Replace(text, @"(?i)(OpenClaw-Debugger-Private|OpenClaw-Server-Backup|UploadStaging|ThumbnailCache|Rollback|Exports|Secrets)", "<private>");
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    public sealed class OperationLogScope : IDisposable
    {
        private readonly OperationLogStore _store;
        private readonly OperationContext _context;
        private int _completed;

        internal OperationLogScope(OperationLogStore store, OperationContext context)
        {
            _store = store;
            _context = context;
        }

        public void Complete(string stage = "completed", bool success = true, string? message = null, int retryCount = 0)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _store.Complete(_context, stage, success, message, retryCount);
        }

        public void Dispose() => Complete("completed", true);
    }

    internal sealed record OperationContext(
        string OperationId,
        string Operation,
        DateTimeOffset StartedAt,
        long StartTimestamp);
}
