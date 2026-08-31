using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;

namespace ClaudeCodeOverview.Tests;

public class CostCalculatorTests
{
    private readonly CostCalculator _calc = new(CostCalculator.DefaultSeed);

    [Fact]
    public void Computes_cost_from_all_five_rates()
    {
        // claude-fable-5: in 10, out 50, w5m 12.50, w1h 20, read 1 (USD/MTok)
        var (cost, _) = _calc.Compute("claude-fable-5",
            input: 1_000_000, output: 1_000_000, cacheCreation: 2_000_000, cacheRead: 1_000_000,
            cache5m: 1_000_000, cache1h: 1_000_000);
        Assert.NotNull(cost);
        Assert.Equal(10 + 50 + 12.50 + 20 + 1, cost!.Value, precision: 6);
    }

    [Fact]
    public void Net_savings_subtracts_write_premiums()
    {
        // reads save (in - read); writes cost (w - in) extra.
        var (_, savings) = _calc.Compute("claude-opus-5",
            input: 0, output: 0, cacheCreation: 2_000_000, cacheRead: 10_000_000,
            cache5m: 1_000_000, cache1h: 1_000_000);
        // 10M * (5 - 0.5)/M = 45; premiums: 1M*(6.25-5)/M = 1.25 and 1M*(10-5)/M = 5
        Assert.Equal(45 - 1.25 - 5, savings!.Value, precision: 6);
    }

    [Fact]
    public void Missing_ttl_breakdown_falls_back_to_5m_rate()
    {
        var (withBreakdown, _) = _calc.Compute("claude-haiku-4-5", 0, 0, 1_000_000, 0, 1_000_000, 0);
        var (withoutBreakdown, _) = _calc.Compute("claude-haiku-4-5", 0, 0, 1_000_000, 0, 0, 0);
        Assert.Equal(withBreakdown, withoutBreakdown);
    }

    [Fact]
    public void Unknown_model_returns_null_never_zero()
    {
        var (cost, savings) = _calc.Compute("claude-hypothetical-9", 1000, 1000, 0, 0, 0, 0);
        Assert.Null(cost);
        Assert.Null(savings);
    }

    [Fact]
    public void Longest_prefix_wins()
    {
        var calc = new CostCalculator([
            new PricingRow("claude-opus", 1, 1, 1, 1, 1),
            new PricingRow("claude-opus-4", 2, 2, 2, 2, 2),
        ]);
        Assert.Equal("claude-opus-4", calc.Match("claude-opus-4-8")!.ModelPattern);
        Assert.Equal("claude-opus", calc.Match("claude-opus-5")!.ModelPattern);
    }
}

public class BlockCalculatorTests
{
    private static (DateTimeOffset, long, double?) Ev(string ts, long tokens = 100) =>
        (DateTimeOffset.Parse(ts), tokens, null);

    [Fact]
    public void Block_starts_at_first_event_floored_to_hour()
    {
        var blocks = BlockCalculator.Cluster([Ev("2026-08-31T10:42:00Z")]);
        var b = Assert.Single(blocks);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T10:00:00Z"), b.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T15:00:00Z"), b.EndUtc);
    }

    [Fact]
    public void Gap_beyond_five_hours_starts_a_new_block()
    {
        var blocks = BlockCalculator.Cluster([
            Ev("2026-08-31T08:10:00Z"),
            Ev("2026-08-31T12:59:00Z"),  // within 08:00+5h
            Ev("2026-08-31T13:30:00Z"),  // ≥ 13:00 → new block floored to 13:00
        ]);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T08:00:00Z"), blocks[0].StartUtc);
        Assert.Equal(2, blocks[0].Messages);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T13:00:00Z"), blocks[1].StartUtc);
    }

    [Fact]
    public void Out_of_order_arrival_produces_identical_blocks()
    {
        var ordered = new[] { Ev("2026-08-31T08:10:00Z"), Ev("2026-08-31T09:00:00Z"), Ev("2026-08-31T16:00:00Z") };
        var shuffled = new[] { ordered[2], ordered[0], ordered[1] };
        Assert.Equal(BlockCalculator.Cluster(ordered), BlockCalculator.Cluster(shuffled));
    }

    [Fact]
    public void Rebuild_all_persists_blocks_from_usage_events()
    {
        using var db = new TestDb();
        db.Connection.Execute(
            """
            INSERT INTO usage_events(message_id, session_id, project_id, ts_utc, day_local, model,
                                     input_tokens, output_tokens, cache_creation, cache_read)
            VALUES ('m1','s',1,'2026-08-31T08:10:00.000Z','2026-08-31','claude-opus-5',10,20,0,0),
                   ('m2','s',1,'2026-08-31T20:10:00.000Z','2026-08-31','claude-opus-5',5,5,0,0)
            """);
        BlockCalculator.RebuildAll(db.Connection);

        var blocks = db.Connection.Query<(string Start, long Tokens, int Messages)>(
            "SELECT block_start_utc, tokens, messages FROM activity_blocks ORDER BY block_start_utc").ToList();
        Assert.Equal(2, blocks.Count);
        Assert.Equal(30, blocks[0].Tokens);
        Assert.Equal(1, blocks[0].Messages);
    }
}

public class TimeBucketsTests
{
    [Theory]
    [InlineData("2026-06-30T23:30:00Z", "2026-07-01")] // CEST = UTC+2
    [InlineData("2026-01-15T23:30:00Z", "2026-01-16")] // CET = UTC+1
    [InlineData("2026-01-15T22:30:00Z", "2026-01-15")]
    public void Amsterdam_day_bucketing_handles_dst(string utc, string expectedDay) =>
        Assert.Equal(expectedDay, TimeBuckets.DayLocal(DateTimeOffset.Parse(utc)));
}
