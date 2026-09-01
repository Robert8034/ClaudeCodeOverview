using ClaudeCodeOverview.Core.Data;
using ClaudeCodeOverview.Core.Queries;
using Dapper;

namespace ClaudeCodeOverview.Tests;

/// <summary>
/// The other half of <see cref="EmptyDatabaseTests"/>. Those prove no query throws on an empty
/// database; these prove no query silently returns zeros on a populated one.
///
/// That failure mode is specific to the fix those tests describe: [method: ExplicitConstructor]
/// makes Dapper bind by parameter NAME, and an unmatched name yields default(T) instead of an
/// exception. A typo'd column alias would therefore render as 0 in the UI rather than blowing up,
/// so every DTO needs at least one real value asserted through the query seam.
/// </summary>
public class PopulatedDatabaseTests
{
    private static (TestDb Db, DashboardQueries Q) Seed()
    {
        var db = new TestDb();
        var repo = new IngestRepository();

        // Real sanitized transcripts: a whole session, one of every record shape, and a tool pair.
        Fixtures.Ingest(db, repo, "session_small.jsonl");
        Fixtures.Ingest(db, repo, "records_various.jsonl");
        Fixtures.Ingest(db, repo, "tool_pair.jsonl");

        // Gaps the fixtures cannot cover: an agent with a type (its sidecar lives elsewhere),
        // a successful git commit, and a parse-error row.
        db.Connection.Execute(
            """
            UPDATE agents SET agent_type = 'general-purpose', spawn_depth = 1
            WHERE agent_type IS NULL;

            INSERT INTO tool_events(tool_use_id, session_id, project_id, ts_utc, day_local,
                                    tool_name, is_error, is_git_commit, lines_added, lines_removed)
            SELECT 'toolu_synthetic_commit', session_id, project_id, ts_utc, day_local,
                   'Bash', 0, 1, 12, 3
            FROM tool_events LIMIT 1;

            INSERT INTO parse_error_log(file, line_no, ts_utc, snippet)
            VALUES('fixture.jsonl', 42, '2026-09-01T00:00:00Z', '{"type":"broken"');

            -- Every type in the fixtures is a known one, so stand in for a future CLI release
            -- emitting a record type this build has never seen.
            INSERT INTO record_stats(file_id, record_type, cnt)
            SELECT id, 'brand-new-type', 7 FROM ingested_files LIMIT 1;
            """);

        return (db, new DashboardQueries(db.Path));
    }

    [Fact]
    public async Task Every_query_binds_real_values_not_defaults()
    {
        var (db, q) = Seed();
        using var _ = db;
        var all = new QueryFilter();

        var headline = await q.GetHeadlineStatsAsync(all);
        Assert.True(headline.Turns > 0, "turns");
        Assert.True(headline.Sessions > 0, "sessions");
        Assert.True(headline.InputTokens > 0 && headline.OutputTokens > 0, "token split");
        Assert.True(headline.CacheRead > 0, "cache read");
        Assert.True(headline.CostUsd > 0, "cost");

        var daily = await q.GetDailyByModelAsync(all);
        Assert.NotEmpty(daily);
        Assert.All(daily, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.DayLocal));
            Assert.False(string.IsNullOrEmpty(d.Key));
        });
        Assert.True(daily.Sum(d => d.Tokens) > 0, "daily tokens");

        var byProject = await q.GetDailyByProjectAsync(all);
        Assert.NotEmpty(byProject);
        Assert.All(byProject, d => Assert.False(string.IsNullOrEmpty(d.Key)));
        Assert.True(byProject.Sum(d => d.Tokens) > 0, "project tokens");

        var mix = await q.GetModelMixAsync(all);
        Assert.NotEmpty(mix);
        Assert.All(mix, m => Assert.False(string.IsNullOrEmpty(m.Model)));
        Assert.True(mix.Sum(m => m.Tokens) > 0, "mix tokens");
        Assert.True(mix.Sum(m => m.Turns) > 0, "mix turns");

        var projects = await q.GetProjectSummariesAsync(all);
        Assert.NotEmpty(projects);
        Assert.All(projects, p =>
        {
            Assert.False(string.IsNullOrEmpty(p.Cwd));
            Assert.True(p.ProjectId > 0);
            Assert.False(string.IsNullOrEmpty(p.LastActivityUtc));
        });
        Assert.True(projects.Sum(p => p.Tokens) > 0, "project summary tokens");
        Assert.True(projects.Sum(p => p.Sessions) > 0, "project summary sessions");

        var biggest = projects.OrderByDescending(p => p.Tokens).First();
        var sessions = await q.GetSessionsAsync(all, biggest.ProjectId);
        Assert.NotEmpty(sessions);
        Assert.All(sessions, s => Assert.False(string.IsNullOrEmpty(s.SessionId)));
        Assert.True(sessions.Sum(s => s.Turns) > 0, "session turns");
        Assert.True(sessions.Sum(s => s.Tokens) > 0, "session tokens");
        Assert.Contains(sessions, s => !string.IsNullOrEmpty(s.FirstTsUtc) && !string.IsNullOrEmpty(s.LastTsUtc));

        var detail = await q.GetSessionDetailAsync(sessions[0].SessionId);
        Assert.NotNull(detail);
        Assert.NotEmpty(detail!.Turns);
        Assert.All(detail.Turns, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.TsUtc));
            Assert.False(string.IsNullOrEmpty(t.Model));
        });
        Assert.True(detail.Turns.Sum(t => t.OutputTokens) > 0, "turn tokens");

        var builtins = await q.GetBuiltinCommandsAsync(all);
        Assert.NotEmpty(builtins);
        Assert.All(builtins, b =>
        {
            Assert.False(string.IsNullOrEmpty(b.CommandName));
            Assert.True(b.Invocations > 0);
        });

        var tools = await q.GetToolUsageAsync(all);
        Assert.NotEmpty(tools);
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.ToolName));
            Assert.True(t.Calls > 0);
        });
        Assert.Contains(tools, t => t.Errors > 0);          // tool_pair.jsonl carries a real failure
        Assert.Contains(tools, t => t.ErrorRate > 0);

        var agents = await q.GetAgentUsageAsync(all);
        Assert.NotEmpty(agents);
        Assert.All(agents, a =>
        {
            Assert.False(string.IsNullOrEmpty(a.AgentType));
            Assert.True(a.Spawns > 0);
        });

        var heatmap = await q.GetActivityHeatmapAsync("2020-01-01", "2030-12-31");
        Assert.NotEmpty(heatmap);
        Assert.All(heatmap, c => Assert.False(string.IsNullOrEmpty(c.DayLocal)));
        Assert.True(heatmap.Sum(c => c.Sessions) > 0, "heatmap sessions");
        Assert.True(heatmap.Sum(c => c.Tokens) > 0, "heatmap tokens");

        var productivity = await q.GetProductivityDailyAsync(all);
        Assert.NotEmpty(productivity);
        Assert.True(productivity.Sum(p => p.Commits) > 0, "commits");
        Assert.True(productivity.Sum(p => p.LinesAdded) > 0, "lines added");

        var durations = await q.GetSessionDurationHistogramAsync(all);
        Assert.NotEmpty(durations);
        Assert.All(durations, d => Assert.False(string.IsNullOrEmpty(d.Label)));
        Assert.True(durations.Sum(d => d.SessionCount) > 0, "duration histogram");

        var health = await q.GetDataHealthAsync();
        Assert.True(health.UsageEventCount > 0, "usage rows");
        Assert.True(health.FilesActive > 0, "ingested files");
        Assert.Equal(7, health.UnknownRecordTypes["brand-new-type"]);
        var parseError = Assert.Single(health.RecentParseErrors);
        Assert.Equal("fixture.jsonl", parseError.File);
        Assert.Equal(42, parseError.LineNo);
        Assert.False(string.IsNullOrEmpty(parseError.Snippet));

        Assert.NotEmpty(await q.GetKnownModelsAsync());
        Assert.NotEmpty(await q.GetProjectsAsync());
    }

    [Fact]
    public async Task Skill_queries_bind_real_values()
    {
        var (db, q) = Seed();
        using var _ = db;
        var all = new QueryFilter();

        // records_various.jsonl carries an in-session skill record and a forked-skill launch.
        var scorecard = await q.GetSkillScorecardAsync(all, "2026-09-01");
        Assert.NotEmpty(scorecard);
        Assert.All(scorecard, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.SkillName));
            Assert.False(s.SkillName.StartsWith('/'));   // normalized across shapes
            Assert.False(string.IsNullOrEmpty(s.Shapes));
            Assert.True(s.Invocations > 0);
        });

        var skill = scorecard[0].SkillName;
        var daily = await q.GetSkillDailyAsync(all, skill);
        Assert.NotEmpty(daily);
        Assert.All(daily, d => Assert.False(string.IsNullOrEmpty(d.DayLocal)));
        Assert.True(daily.Sum(d => d.Invocations) > 0, "skill invocations");
    }

    [Fact]
    public async Task Rate_windows_bind_block_rows()
    {
        var (db, q) = Seed();
        using var _ = db;

        // The fixtures are historical, so ask as of the newest event rather than "now".
        var newest = DateTimeOffset.Parse(
            db.Connection.ExecuteScalar<string>("SELECT MAX(ts_utc) FROM usage_events")!);

        var windows = await q.GetRateWindowsAsync(newest);
        Assert.NotEmpty(windows.RecentBlocks);
        Assert.All(windows.RecentBlocks, b =>
        {
            Assert.False(string.IsNullOrEmpty(b.StartUtc));
            Assert.False(string.IsNullOrEmpty(b.EndUtc));
            Assert.True(b.Messages > 0);
            Assert.True(b.Tokens > 0);
        });
    }
}
