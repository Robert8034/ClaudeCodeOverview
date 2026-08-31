using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// FileSystemWatcher (inotify on Linux) over the transcript root with per-path trailing
/// debounce, feeding one channel consumed by the single ingestion writer. FSW can drop
/// events under load — the periodic rescan in IngestionService is the safety net.
/// </summary>
public sealed class TranscriptWatcher : IDisposable
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Timer> _debouncers = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _fsw;
    private readonly int _debounceMs;
    private readonly ILogger _logger;

    public TranscriptWatcher(string dataRoot, int debounceMs, ILogger logger)
    {
        _debounceMs = Math.Max(50, debounceMs);
        _logger = logger;
        _fsw = new FileSystemWatcher(dataRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Changed += (_, e) => OnEvent(e.FullPath);
        _fsw.Created += (_, e) => OnEvent(e.FullPath);
        _fsw.Renamed += (_, e) => OnEvent(e.FullPath);
        _fsw.Error += (_, e) => _logger.LogWarning(e.GetException(), "FileSystemWatcher error (rescan will catch up)");
    }

    public ChannelReader<string> Reader => _channel.Reader;

    public void Start() => _fsw.EnableRaisingEvents = true;

    /// <summary>Used by backfill and rescans to route work through the same single consumer.</summary>
    public void Enqueue(string path) => _channel.Writer.TryWrite(path);

    private void OnEvent(string path)
    {
        if (!TranscriptPaths.IsIngestible(path)) return;
        var timer = _debouncers.GetOrAdd(path, p => new Timer(_ =>
        {
            if (_debouncers.TryRemove(p, out var t)) t.Dispose();
            _channel.Writer.TryWrite(p);
        }, null, Timeout.Infinite, Timeout.Infinite));
        try
        {
            timer.Change(_debounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Raced with its own firing; the write already happened or the rescan catches it.
        }
    }

    public void Dispose()
    {
        _fsw.Dispose();
        foreach (var t in _debouncers.Values) t.Dispose();
        _debouncers.Clear();
        _channel.Writer.TryComplete();
    }
}
