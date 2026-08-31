using ClaudeCodeOverview.Core.Data;
using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Ingestion;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Core.Queries;

/// <summary>
/// Read-side queries. Opens short-lived connections per call (WAL: readers don't block the
/// single ingestion writer). UpsertPricingAsync is the one write path here — rare, and WAL +
/// busy_timeout make the brief second writer safe.
/// </summary>
public sealed class DashboardQueries(string dbPath) : IDashboardQueries
{
    private SqliteConnection Open() => Db.Open(dbPath);

    private static (string Where, DynamicParameters Params) Filter(QueryFilter f, string col = "day_local")
    {
        var clauses = new List<string> { "1=1" };
        var p = new DynamicParameters();
        if (f.FromDayLocal is not null) { clauses.Add($"{col} >= @from"); p.Add("from", f.FromDayLocal); }
        if (f.ToDayLocal is not null) { clauses.Add($"{col} <= @to"); p.Add("to", f.ToDayLocal); }
        if (f.ProjectIds is { Length: > 0 }) { clauses.Add("project_id IN @pids"); p.Add("pids", f.ProjectIds); }
        if (f.Models is { Length: > 0 }) { clauses.Add("model IN @models"); p.Add("models", f.Models); }
        return (string.Join(" AND ", clauses), p);
    }

    public async Task<HeadlineStats> GetHeadlineStatsAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var row = await conn.QuerySingleAsync<(long?, long?, long?, long?, long?, long?, double?, double?, long, long)>(
            $"""
             SELECT SUM(input_tokens), SUM(output_tokens), SUM(cache_creation), SUM(cache_read),
                    SUM(cache_5m), SUM(cache_1h), SUM(cost_usd), SUM(cache_savings_usd),
                    COUNT(DISTINCT session_id), COUNT(*)
             FROM usage_events WHERE {where}
             """, p);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var active = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT session_id) FROM usage_events WHERE ts_utc >= @cutoff", new { cutoff });

        return new HeadlineStats(
            row.Item1 ?? 0, row.Item2 ?? 0, row.Item3 ?? 0, row.Item4 ?? 0,
            row.Item5 ?? 0, row.Item6 ?? 0, row.Item7 ?? 0, row.Item8 ?? 0,
            row.Item9, row.Item10, active);
    }

    public async Task<List<DailyPoint>> GetDailyByModelAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<DailyPoint>(
            $"""
             SELECT day_local AS DayLocal, model AS Key,
                    SUM(input_tokens + output_tokens + cache_creation + cache_read) AS Tokens,
                    COALESCE(SUM(cost_usd), 0) AS CostUsd
             FROM usage_events WHERE {where}
             GROUP BY day_local, model ORDER BY day_local
             """, p);
        return rows.ToList();
    }

    public async Task<List<DailyPoint>> GetDailyByProjectAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<DailyPoint>(
            $"""
             SELECT u.day_local AS DayLocal, COALESCE(pr.slug, pr.cwd) AS Key,
                    SUM(u.input_tokens + u.output_tokens + u.cache_creation + u.cache_read) AS Tokens,
                    COALESCE(SUM(u.cost_usd), 0) AS CostUsd
             FROM usage_events u JOIN projects pr ON pr.id = u.project_id
             WHERE {where}
             GROUP BY u.day_local, pr.id ORDER BY u.day_local
             """, p);
        return rows.ToList();
    }

    public async Task<List<ModelMixRow>> GetModelMixAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<(string Model, long Tokens, double? Cost, long Turns, long Unpriced)>(
            $"""
             SELECT model, SUM(input_tokens + output_tokens + cache_creation + cache_read),
                    SUM(cost_usd), COUNT(*), SUM(CASE WHEN cost_usd IS NULL THEN 1 ELSE 0 END)
             FROM usage_events WHERE {where}
             GROUP BY model ORDER BY 2 DESC
             """, p);
        return rows.Select(r => new ModelMixRow(r.Model, r.Tokens, r.Cost ?? 0, r.Turns, r.Unpriced > 0)).ToList();
    }

    public async Task<List<ProjectSummary>> GetProjectSummariesAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<ProjectSummary>(
            $"""
             SELECT pr.id AS ProjectId, pr.cwd AS Cwd, pr.slug AS Slug,
                    SUM(u.input_tokens + u.output_tokens + u.cache_creation + u.cache_read) AS Tokens,
                    COALESCE(SUM(u.cost_usd), 0) AS CostUsd,
                    COUNT(DISTINCT u.session_id) AS Sessions,
                    MAX(u.ts_utc) AS LastActivityUtc
             FROM usage_events u JOIN projects pr ON pr.id = u.project_id
             WHERE {where}
             GROUP BY pr.id ORDER BY Tokens DESC
             """, p);
        return rows.ToList();
    }

    public async Task<List<SessionSummary>> GetSessionsAsync(QueryFilter f, long projectId)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        p.Add("projectId", projectId);
        var rows = await conn.QueryAsync<SessionSummary>(
            $"""
             SELECT s.id AS SessionId, s.title AS Title, s.git_branch AS GitBranch,
                    s.first_ts_utc AS FirstTsUtc, s.last_ts_utc AS LastTsUtc,
                    COUNT(u.message_id) AS Turns,
                    SUM(u.input_tokens + u.output_tokens + u.cache_creation + u.cache_read) AS Tokens,
                    COALESCE(SUM(u.cost_usd), 0) AS CostUsd,
                    COUNT(DISTINCT u.agent_id) AS SubagentCount,
                    (SELECT COUNT(*) FROM tool_events t WHERE t.session_id = s.id AND t.is_error = 1) AS ToolErrors
             FROM sessions s
             JOIN usage_events u ON u.session_id = s.id
             WHERE u.project_id = @projectId AND {where}
             GROUP BY s.id ORDER BY s.last_ts_utc DESC
             """, p);
        return rows.ToList();
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(string sessionId)
    {
        using var conn = Open();
        var head = await conn.QuerySingleOrDefaultAsync<(string Id, string? Title, string? Branch, string? Ver)?>(
            "SELECT id, title, git_branch, cli_version FROM sessions WHERE id=@sessionId", new { sessionId });
        if (head is null) return null;

        var turns = (await conn.QueryAsync<SessionTurn>(
            """
            SELECT ts_utc AS TsUtc, model AS Model, effort AS Effort,
                   input_tokens AS InputTokens, output_tokens AS OutputTokens,
                   cache_creation AS CacheCreation, cache_read AS CacheRead,
                   cost_usd AS CostUsd, agent_id AS AgentId, attribution_skill AS AttributionSkill
            FROM usage_events WHERE session_id=@sessionId ORDER BY ts_utc
            """, new { sessionId })).ToList();

        var agents = (await conn.QueryAsync<SessionAgent>(
            """
            SELECT a.agent_id AS AgentId, a.parent_agent_id AS ParentAgentId, a.agent_type AS AgentType,
                   a.description AS Description, a.spawn_depth AS SpawnDepth, a.workflow_id AS WorkflowId,
                   a.skill_name AS SkillName,
                   COALESCE(SUM(u.input_tokens + u.output_tokens + u.cache_creation + u.cache_read), 0) AS Tokens,
                   COALESCE(SUM(u.cost_usd), 0) AS CostUsd
            FROM agents a
            LEFT JOIN usage_events u ON u.agent_id = a.agent_id AND u.session_id = a.session_id
            WHERE a.session_id=@sessionId
            GROUP BY a.agent_id
            """, new { sessionId })).ToList();

        var skills = (await conn.QueryAsync<string>(
            "SELECT DISTINCT skill_name FROM skill_invocations WHERE session_id=@sessionId ORDER BY skill_name",
            new { sessionId })).ToList();

        return new SessionDetail(head.Value.Id, head.Value.Title, head.Value.Branch, head.Value.Ver, turns, agents, skills);
    }

    public async Task<List<SkillScorecardRow>> GetSkillScorecardAsync(QueryFilter f, string todayLocal)
    {
        using var conn = Open();
        var (where, p) = Filter(f);

        // Scorecard = real skills only; built-in slash commands live in GetBuiltinCommandsAsync.
        var invocations = (await conn.QueryAsync<(string Skill, string Shape, long Cnt)>(
            $"""
             SELECT skill_name, shape, COUNT(*) FROM skill_invocations
             WHERE shape != 'local_command' AND {where}
             GROUP BY skill_name, shape
             """, p)).ToList();

        var today = TranscriptLineParser.ParseTimestamp(todayLocal + "T00:00:00Z") ?? DateTimeOffset.UtcNow;
        var d30 = today.AddDays(-30).ToString("yyyy-MM-dd");
        var d60 = today.AddDays(-60).ToString("yyyy-MM-dd");
        var trend = (await conn.QueryAsync<(string Skill, long Last30, long Prior30)>(
            """
            SELECT skill_name,
                   SUM(CASE WHEN day_local >= @d30 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN day_local >= @d60 AND day_local < @d30 THEN 1 ELSE 0 END)
            FROM skill_invocations WHERE shape != 'local_command'
            GROUP BY skill_name
            """, new { d30, d60 })).ToDictionary(r => r.Skill, r => (r.Last30, r.Prior30));

        var attributed = (await conn.QueryAsync<(string Skill, long Tokens, double? Cost)>(
            $"""
             SELECT attribution_skill,
                    SUM(input_tokens + output_tokens + cache_creation + cache_read), SUM(cost_usd)
             FROM usage_events WHERE attribution_skill IS NOT NULL AND {where}
             GROUP BY attribution_skill
             """, p)).ToDictionary(r => r.Skill, r => (r.Tokens, r.Cost ?? 0));

        var toolStats = (await conn.QueryAsync<(string Skill, long Calls, long Errors)>(
            """
            SELECT a.skill_name, COUNT(*), SUM(t.is_error)
            FROM tool_events t JOIN agents a ON a.agent_id = t.agent_id
            WHERE a.skill_name IS NOT NULL
            GROUP BY a.skill_name
            """)).ToDictionary(r => r.Skill, r => (r.Calls, r.Errors));

        var durations = (await conn.QueryAsync<(string Skill, string MinTs, string MaxTs)>(
            """
            SELECT a.skill_name, MIN(u.ts_utc), MAX(u.ts_utc)
            FROM usage_events u JOIN agents a ON a.agent_id = u.agent_id
            WHERE a.skill_name IS NOT NULL
            GROUP BY a.agent_id
            """)).ToList();
        var medians = durations
            .GroupBy(d => d.Skill)
            .ToDictionary(g => g.Key, g => Median(g
                .Select(d => (TranscriptLineParser.ParseTimestamp(d.MaxTs) - TranscriptLineParser.ParseTimestamp(d.MinTs))?.TotalSeconds)
                .Where(s => s is not null).Select(s => s!.Value).ToList()));

        return invocations
            .GroupBy(i => i.Skill)
            .Select(g =>
            {
                var (last30, prior30) = trend.GetValueOrDefault(g.Key);
                var (tokens, cost) = attributed.GetValueOrDefault(g.Key);
                var (calls, errors) = toolStats.GetValueOrDefault(g.Key);
                return new SkillScorecardRow(
                    g.Key, g.Sum(x => x.Cnt), last30, prior30,
                    string.Join("+", g.Select(x => x.Shape).Distinct().Order()),
                    tokens, cost, calls, errors, medians.GetValueOrDefault(g.Key));
            })
            .OrderByDescending(r => r.Invocations)
            .ToList();
    }

    public async Task<List<BuiltinCommandRow>> GetBuiltinCommandsAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<BuiltinCommandRow>(
            $"""
             SELECT skill_name AS CommandName, COUNT(*) AS Invocations, MAX(ts_utc) AS LastUsedUtc
             FROM skill_invocations WHERE shape = 'local_command' AND {where}
             GROUP BY skill_name ORDER BY Invocations DESC
             """, p);
        return rows.ToList();
    }

    public async Task<List<SkillDailyPoint>> GetSkillDailyAsync(QueryFilter f, string skillName)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        p.Add("skill", skillName);
        var inv = (await conn.QueryAsync<(string Day, long Cnt)>(
            $"SELECT day_local, COUNT(*) FROM skill_invocations WHERE skill_name=@skill AND {where} GROUP BY day_local",
            p)).ToDictionary(r => r.Day, r => r.Cnt);
        var tok = (await conn.QueryAsync<(string Day, long Tokens)>(
            $"""
             SELECT day_local, SUM(input_tokens + output_tokens + cache_creation + cache_read)
             FROM usage_events WHERE attribution_skill=@skill AND {where} GROUP BY day_local
             """, p)).ToDictionary(r => r.Day, r => r.Tokens);
        return inv.Keys.Union(tok.Keys).Order()
            .Select(d => new SkillDailyPoint(d, inv.GetValueOrDefault(d), tok.GetValueOrDefault(d)))
            .ToList();
    }

    public async Task<List<ToolUsageRow>> GetToolUsageAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<(string Name, long Calls, long Errors, long Mcp)>(
            $"""
             SELECT tool_name, COUNT(*), SUM(is_error), MAX(is_mcp)
             FROM tool_events WHERE {where}
             GROUP BY tool_name ORDER BY 2 DESC
             """, p);
        return rows.Select(r => new ToolUsageRow(
            r.Name, r.Calls, r.Errors, r.Calls == 0 ? 0 : (double)r.Errors / r.Calls, r.Mcp == 1)).ToList();
    }

    public async Task<List<AgentUsageRow>> GetAgentUsageAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f, "u.day_local");
        var rows = await conn.QueryAsync<AgentUsageRow>(
            $"""
             SELECT COALESCE(a.agent_type, '(unknown)') AS AgentType,
                    COUNT(DISTINCT a.agent_id) AS Spawns,
                    COALESCE(SUM(u.input_tokens + u.output_tokens + u.cache_creation + u.cache_read), 0) AS Tokens,
                    COALESCE(SUM(u.cost_usd), 0) AS CostUsd
             FROM agents a
             LEFT JOIN usage_events u ON u.agent_id = a.agent_id
             WHERE u.agent_id IS NULL OR {where}
             GROUP BY COALESCE(a.agent_type, '(unknown)') ORDER BY Tokens DESC
             """, p);
        return rows.ToList();
    }

    public async Task<List<HeatmapCell>> GetActivityHeatmapAsync(string fromDayLocal, string toDayLocal)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<HeatmapCell>(
            """
            SELECT day_local AS DayLocal, COUNT(DISTINCT session_id) AS Sessions,
                   SUM(input_tokens + output_tokens + cache_creation + cache_read) AS Tokens,
                   COALESCE(SUM(cost_usd), 0) AS CostUsd
            FROM usage_events WHERE day_local BETWEEN @fromDayLocal AND @toDayLocal
            GROUP BY day_local
            """, new { fromDayLocal, toDayLocal });
        return rows.ToList();
    }

    public async Task<RateWindows> GetRateWindowsAsync(DateTimeOffset nowUtc)
    {
        using var conn = Open();
        var cut5h = nowUtc.AddHours(-5).ToString("O");
        var cut7d = nowUtc.AddDays(-7).ToString("O");

        var cur = await conn.QuerySingleAsync<(long? Tokens, double? Cost)>(
            """
            SELECT SUM(input_tokens + output_tokens + cache_creation + cache_read), SUM(cost_usd)
            FROM usage_events WHERE ts_utc >= @cut5h
            """, new { cut5h });
        var week = await conn.QuerySingleAsync<(long? Tokens, double? Cost)>(
            """
            SELECT SUM(input_tokens + output_tokens + cache_creation + cache_read), SUM(cost_usd)
            FROM usage_events WHERE ts_utc >= @cut7d
            """, new { cut7d });
        var blocks = (await conn.QueryAsync<BlockInfo>(
            """
            SELECT block_start_utc AS StartUtc, block_end_utc AS EndUtc, tokens AS Tokens,
                   cost_usd AS CostUsd, messages AS Messages
            FROM activity_blocks WHERE block_end_utc >= @cut7d ORDER BY block_start_utc
            """, new { cut7d })).ToList();

        return new RateWindows(cur.Tokens ?? 0, cur.Cost ?? 0, week.Tokens ?? 0, week.Cost ?? 0, blocks);
    }

    public async Task<List<ProductivityDay>> GetProductivityDailyAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f);
        var rows = await conn.QueryAsync<ProductivityDay>(
            $"""
             SELECT day_local AS DayLocal,
                    COALESCE(SUM(lines_added), 0) AS LinesAdded,
                    COALESCE(SUM(lines_removed), 0) AS LinesRemoved,
                    SUM(CASE WHEN is_git_commit = 1 AND is_error = 0 THEN 1 ELSE 0 END) AS Commits
             FROM tool_events WHERE {where}
             GROUP BY day_local ORDER BY day_local
             """, p);
        return rows.ToList();
    }

    public async Task<List<DurationBucket>> GetSessionDurationHistogramAsync(QueryFilter f)
    {
        using var conn = Open();
        var (where, p) = Filter(f, "u.day_local");
        var rows = (await conn.QueryAsync<(string? First, string? Last)>(
            $"""
             SELECT s.first_ts_utc, s.last_ts_utc FROM sessions s
             WHERE EXISTS (SELECT 1 FROM usage_events u WHERE u.session_id = s.id AND {where})
             """, p)).ToList();

        (string Label, double MaxMinutes)[] buckets =
            [("< 5 min", 5), ("5–15 min", 15), ("15–60 min", 60), ("1–3 h", 180), ("3+ h", double.MaxValue)];
        var counts = new long[buckets.Length];
        foreach (var (first, last) in rows)
        {
            var d = TranscriptLineParser.ParseTimestamp(last) - TranscriptLineParser.ParseTimestamp(first);
            if (d is null) continue;
            var minutes = d.Value.TotalMinutes;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (minutes < buckets[i].MaxMinutes) { counts[i]++; break; }
            }
        }
        return buckets.Select((b, i) => new DurationBucket(b.Label, counts[i])).ToList();
    }

    public async Task<DataHealth> GetDataHealthAsync()
    {
        using var conn = Open();
        var files = await conn.QuerySingleAsync<(long Active, long Deleted, long Error, long? Pe, long? Ut)>(
            """
            SELECT SUM(CASE WHEN status='active' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN status='deleted' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN status='error' THEN 1 ELSE 0 END),
                   SUM(parse_errors), SUM(unknown_types)
            FROM ingested_files
            """);
        var unknownTypes = (await conn.QueryAsync<(string Type, long Cnt)>(
                "SELECT record_type, SUM(cnt) FROM record_stats GROUP BY record_type"))
            .Where(r => !ParsedBatch.KnownTypes.Contains(r.Type))
            .ToDictionary(r => r.Type, r => r.Cnt);
        var unknownModels = (await conn.QueryAsync<string>(
            "SELECT DISTINCT model FROM usage_events WHERE cost_usd IS NULL ORDER BY model")).ToList();
        var recentErrors = (await conn.QueryAsync<ParseErrorRow>(
            """
            SELECT file AS File, line_no AS LineNo, ts_utc AS TsUtc, snippet AS Snippet
            FROM parse_error_log ORDER BY id DESC LIMIT 50
            """)).ToList();
        var lastIngest = await conn.ExecuteScalarAsync<string?>(
            "SELECT MAX(last_write_utc) FROM ingested_files WHERE status='active'");
        var usageCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM usage_events");

        return new DataHealth(
            files.Active, files.Deleted, files.Error, files.Pe ?? 0, files.Ut ?? 0,
            unknownTypes, unknownModels, recentErrors, lastIngest, usageCount);
    }

    public async Task<List<PricingRow>> GetPricingAsync()
    {
        using var conn = Open();
        return new IngestRepository().LoadPricing(conn);
    }

    public async Task UpsertPricingAsync(PricingRow row)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO pricing(model_pattern, in_usd, out_usd, cache_w5m_usd, cache_w1h_usd, cache_r_usd)
            VALUES(@ModelPattern, @InUsd, @OutUsd, @CacheW5mUsd, @CacheW1hUsd, @CacheRUsd)
            ON CONFLICT(model_pattern) DO UPDATE SET
              in_usd=excluded.in_usd, out_usd=excluded.out_usd, cache_w5m_usd=excluded.cache_w5m_usd,
              cache_w1h_usd=excluded.cache_w1h_usd, cache_r_usd=excluded.cache_r_usd
            """, row);
        var repo = new IngestRepository();
        var costs = new CostCalculator(repo.LoadPricing(conn));
        repo.RecomputeCosts(conn, costs);
        BlockCalculator.RebuildAll(conn);
    }

    public async Task<List<string>> GetKnownModelsAsync()
    {
        using var conn = Open();
        return (await conn.QueryAsync<string>("SELECT DISTINCT model FROM usage_events ORDER BY model")).ToList();
    }

    public async Task<List<(long Id, string Cwd)>> GetProjectsAsync()
    {
        using var conn = Open();
        return (await conn.QueryAsync<(long, string)>("SELECT id, cwd FROM projects ORDER BY cwd")).ToList();
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
