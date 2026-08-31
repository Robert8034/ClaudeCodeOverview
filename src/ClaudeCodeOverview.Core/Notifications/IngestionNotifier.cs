namespace ClaudeCodeOverview.Core.Notifications;

public sealed record BackfillProgress(int FilesDone, int FilesTotal);

/// <summary>Coalesced change set the UI refreshes from; one delta per debounce window.</summary>
public sealed class IngestionDelta
{
    public HashSet<long> ProjectIds { get; } = [];
    public HashSet<string> SessionIds { get; } = [];
    public string? MinDayLocal { get; set; }
    public bool NewParseErrors { get; set; }
    public bool NewUnknownModels { get; set; }
    public BackfillProgress? Backfill { get; set; }

    public void MergeDay(string? dayLocal)
    {
        if (dayLocal is null) return;
        if (MinDayLocal is null || string.CompareOrdinal(dayLocal, MinDayLocal) < 0) MinDayLocal = dayLocal;
    }
}

public interface IIngestionNotifier
{
    event Action<IngestionDelta>? Changed;
    /// <summary>Mutates the pending delta; a debounce timer flushes it as one coalesced event.</summary>
    void Publish(Action<IngestionDelta> mutate);
    /// <summary>Flushes immediately (used by tests and on backfill milestones).</summary>
    void Flush();
}

public sealed class IngestionNotifier : IIngestionNotifier, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private IngestionDelta _pending = new();
    private bool _dirty;

    public IngestionNotifier(TimeSpan? debounce = null)
    {
        var interval = debounce ?? TimeSpan.FromSeconds(1.5);
        _timer = new Timer(_ => Flush(), null, interval, interval);
    }

    public event Action<IngestionDelta>? Changed;

    public void Publish(Action<IngestionDelta> mutate)
    {
        lock (_lock)
        {
            mutate(_pending);
            _dirty = true;
        }
    }

    public void Flush()
    {
        IngestionDelta? toSend = null;
        lock (_lock)
        {
            if (_dirty)
            {
                toSend = _pending;
                _pending = new IngestionDelta();
                _dirty = false;
            }
        }
        if (toSend is not null) Changed?.Invoke(toSend);
    }

    public void Dispose() => _timer.Dispose();
}
