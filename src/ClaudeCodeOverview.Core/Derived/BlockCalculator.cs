using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Core.Derived;

/// <summary>
/// 5-hour activity blocks, ccusage semantics: a block starts at the first event's timestamp
/// floored to the hour (UTC); events within start+5h join it; a longer gap starts a new block.
/// Rebuilt from scratch after each batch — backfill and late-arriving subagent files deliver
/// events out of order, which breaks incremental split/merge; a full rebuild is milliseconds
/// at personal scale and always correct.
/// </summary>
public static class BlockCalculator
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(5);

    public sealed record Block(DateTimeOffset StartUtc, DateTimeOffset EndUtc, long Tokens, double? CostUsd, int Messages);

    public static List<Block> Cluster(IEnumerable<(DateTimeOffset Ts, long Tokens, double? Cost)> events)
    {
        var blocks = new List<Block>();
        DateTimeOffset? start = null;
        long tokens = 0;
        double cost = 0;
        var anyCost = false;
        var messages = 0;

        void Flush()
        {
            if (start is null) return;
            blocks.Add(new Block(start.Value, start.Value + Window, tokens, anyCost ? cost : null, messages));
            tokens = 0; cost = 0; anyCost = false; messages = 0;
        }

        foreach (var e in events.OrderBy(e => e.Ts))
        {
            if (start is null || e.Ts >= start.Value + Window)
            {
                Flush();
                start = FloorToHour(e.Ts);
            }
            tokens += e.Tokens;
            messages++;
            if (e.Cost is { } c) { cost += c; anyCost = true; }
        }
        Flush();
        return blocks;
    }

    public static DateTimeOffset FloorToHour(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    public static void RebuildAll(SqliteConnection conn)
    {
        var raw = conn.Query<(string Ts, long Tokens, double? Cost)>(
            """
            SELECT ts_utc, input_tokens + output_tokens + cache_creation + cache_read, cost_usd
            FROM usage_events
            """);

        var events = new List<(DateTimeOffset, long, double?)>();
        foreach (var (ts, tokens, cost) in raw)
        {
            if (DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                events.Add((parsed, tokens, cost));
            }
        }

        var blocks = Cluster(events);

        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM activity_blocks", transaction: tx);
        foreach (var b in blocks)
        {
            conn.Execute(
                """
                INSERT INTO activity_blocks(block_start_utc, block_end_utc, tokens, cost_usd, messages)
                VALUES(@Start, @End, @Tokens, @Cost, @Messages)
                """,
                new
                {
                    Start = b.StartUtc.ToString("O"), End = b.EndUtc.ToString("O"),
                    b.Tokens, Cost = b.CostUsd, b.Messages,
                }, tx);
        }
        tx.Commit();
    }
}
