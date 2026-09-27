namespace OpenClawDebugger;

/// <summary>
/// Coordinates a cooperative pause without cancelling the active SSH stream.
/// The remote tar process remains alive while the local reader waits here.
/// </summary>
public sealed class BackupPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _resumeSignal;
    private bool _paused;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_paused) return false;
            _paused = true;
            _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? signal;
        lock (_gate)
        {
            if (!_paused) return false;
            _paused = false;
            signal = _resumeSignal;
            _resumeSignal = null;
        }
        signal?.TrySetResult(true);
        return true;
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken = default)
    {
        Task? waitTask;
        lock (_gate)
        {
            waitTask = _paused ? _resumeSignal?.Task : null;
        }
        if (waitTask is not null)
            await waitTask.WaitAsync(cancellationToken);
    }
}