using ClaudeCodeOverview.Core.Queries;

namespace ClaudeCodeOverview.Tests;

/// <summary>
/// Every dashboard query must survive a freshly migrated, empty database — that is the
/// fresh-install path (empty data root → setup card, no crashes).
///
/// The trap: with zero rows SQLite cannot type an expression column (SUM/COUNT/…), so
/// Microsoft.Data.Sqlite reports it as BLOB and Dapper refuses to bind a positional record
/// constructor. CAST does not help. Query DTOs therefore carry [method: ExplicitConstructor],
/// which makes Dapper bind by parameter name instead of by reader column type.
/// </summary>
public class EmptyDatabaseTests
{
    private static readonly QueryFilter All = new();

    [Fact]
    public async Task Every_query_returns_empty_results_instead_of_throwing()
    {
        using var db = new TestDb();
        var q = new DashboardQueries(db.Path);

        var headline = await q.GetHeadlineStatsAsync(All);
        Assert.Equal(0, headline.Turns);
        Assert.Equal(0, headline.Sessions);
        Assert.Equal(0d, headline.CostUsd);

        Assert.Empty(await q.GetDailyByModelAsync(All));
        Assert.Empty(await q.GetDailyByProjectAsync(All));
        Assert.Empty(await q.GetModelMixAsync(All));
        Assert.Empty(await q.GetProjectSummariesAsync(All));
        Assert.Empty(await q.GetSessionsAsync(All, projectId: 1));
        Assert.Null(await q.GetSessionDetailAsync("no-such-session"));
        Assert.Empty(await q.GetSkillScorecardAsync(All, "2026-09-01"));
        Assert.Empty(await q.GetBuiltinCommandsAsync(All));
        Assert.Empty(await q.GetSkillDailyAsync(All, "code-review"));
        Assert.Empty(await q.GetToolUsageAsync(All));
        Assert.Empty(await q.GetAgentUsageAsync(All));
        Assert.Empty(await q.GetActivityHeatmapAsync("2026-01-01", "2026-12-31"));
        Assert.Empty(await q.GetProductivityDailyAsync(All));
        Assert.Empty(await q.GetKnownModelsAsync());
        Assert.Empty(await q.GetProjectsAsync());

        var windows = await q.GetRateWindowsAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, windows.Current5hTokens);
        Assert.Equal(0, windows.Rolling7dTokens);
        Assert.Empty(windows.RecentBlocks);

        // Duration buckets are a fixed set of labels; every bucket must simply be empty.
        Assert.All(await q.GetSessionDurationHistogramAsync(All), b => Assert.Equal(0, b.SessionCount));

        var health = await q.GetDataHealthAsync();
        Assert.Equal(0, health.UsageEventCount);
        Assert.Empty(health.RecentParseErrors);
        Assert.Empty(health.UnknownModels);
    }

    [Fact]
    public async Task Pricing_round_trips_on_an_empty_database()
    {
        using var db = new TestDb();
        var q = new DashboardQueries(db.Path);

        Assert.Empty(await q.GetPricingAsync());

        await q.UpsertPricingAsync(new Core.Pricing.PricingRow("claude-test-1", 1, 2, 3, 4, 5));
        var rows = await q.GetPricingAsync();
        // All six rates, not just two: a silent zero on a cache rate would quietly corrupt every
        // cost and savings figure through CostCalculator.
        var row = Assert.Single(rows);
        Assert.Equal("claude-test-1", row.ModelPattern);
        Assert.Equal(1, row.InUsd);
        Assert.Equal(2, row.OutUsd);
        Assert.Equal(3, row.CacheW5mUsd);
        Assert.Equal(4, row.CacheW1hUsd);
        Assert.Equal(5, row.CacheRUsd);
    }
}
