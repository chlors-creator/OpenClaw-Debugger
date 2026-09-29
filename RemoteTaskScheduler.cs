using System.Collections.Concurrent;

namespace OpenClawDebugger;

public enum RemoteTaskState
{
    Queued,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Failed,
    Completed
}

/// <summary>
/// Serializes every operation that can use the remote SSH session. Control commands
/// such as pause/cancel bypass this queue so an active task can be interrupted.
/// </summary>
public sealed class RemoteTaskScheduler : IDisposable
{
    private sealed record Entry(string OperationId, string Command);

    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly object _sync = new();
    private readonly LinkedList<Entry> _waiting = new();
    private readonly ConcurrentDictionary<string, RemoteTaskState> _states = new(StringComparer.Ordinal);
    private readonly Action<RemoteTaskStatus>? _report;
    private Entry? _active;
    private int _disposed;

    public RemoteTaskScheduler(Action<RemoteTaskStatus>? report = null) => _report = report;

    public async Task<object?> EnqueueAsync(
        string operationId,
        string command,
        Func<CancellationToken, Task<object?>> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(operation);

        var entry = new Entry(operationId, command);
        lock (_sync)
        {
            _waiting.AddLast(entry);
            _states[operationId] = RemoteTaskState.Queued;
            Publish(entry, RemoteTaskState.Queued, QueuePosition(entry));
        }

        var acquired = false;
        try
        {
            await _remoteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (_sync)
            {
                _waiting.Remove(entry);
                _active = entry;
                _states[operationId] = RemoteTaskState.Running;
                Publish(entry, RemoteTaskState.Running, 0);
                PublishWaiting();
            }

            var result = await operation(cancellationToken).ConfigureAwait(false);
            _states[operationId] = RemoteTaskState.Completed;
            Publish(entry, RemoteTaskState.Completed, 0);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _states[operationId] = RemoteTaskState.Cancelled;
            Publish(entry, RemoteTaskState.Cancelled, 0, "操作已取消");
            throw;
        }
        catch (Exception error)
        {
            _states[operationId] = RemoteTaskState.Failed;
            Publish(entry, RemoteTaskState.Failed, 0, OperationLogStore.Redact(error.Message));
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _waiting.Remove(entry);
                if (_active == entry) _active = null;
                _states.TryRemove(operationId, out _);
                PublishWaiting();
            }
            if (acquired) _remoteGate.Release();
        }
    }

    public void Report(string operationId, string command, RemoteTaskState state, string? message = null)
    {
        _states[operationId] = state;
        Publish(new Entry(operationId, command), state, 0, message);
    }

    public void ReportActive(RemoteTaskState state, string? message = null)
    {
        lock (_sync)
        {
            if (_active is { } entry) Report(entry.OperationId, entry.Command, state, message);
        }
    }

    private int QueuePosition(Entry entry)
    {
        var position = 0;
        for (var node = _waiting.First; node is not null; node = node.Next)
        {
            if (node.Value == entry) return position;
            position++;
        }
        return 0;
    }

    private void PublishWaiting()
    {
        foreach (var entry in _waiting)
            Publish(entry, RemoteTaskState.Queued, QueuePosition(entry));
    }

    private void Publish(Entry entry, RemoteTaskState state, int queuePosition, string? message = null) =>
        _report?.Invoke(new RemoteTaskStatus(
            entry.OperationId,
            entry.Command,
            state.ToString().ToLowerInvariant(),
            queuePosition,
            DateTimeOffset.UtcNow,
            message));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _remoteGate.Dispose();
    }
}

public sealed record RemoteTaskStatus(
    string OperationId,
    string Command,
    string State,
    int QueuePosition,
    DateTimeOffset TimestampUtc,
    string? Message = null);
