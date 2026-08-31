using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Ingestion;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Core.Data;

public sealed record ApplyResult(
    HashSet<long> ProjectIds,
    HashSet<string> SessionIds,
    string? MinDayLocal,
    bool NewUnknownModels,
    int NewParseErrors);

/// <summary>
/// Applies a ParsedBatch to SQLite in ONE transaction, committing the new byte offset with the
/// rows it produced (crash-safe: either both land or neither). Owned by the single writer.
/// </summary>
public sealed class IngestRepository
{
    private readonly Dictionary<string, long> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    public const string UnknownProjectCwd = "(unknown)";

    public (long FileId, long Offset) EnsureFile(SqliteConnection conn, string path, string? sessionId, string? agentId)
    {
        var row = conn.QuerySingleOrDefault<(long Id, long Offset)?>(
            "SELECT id, byte_offset FROM ingested_files WHERE path=@path", new { path });
        if (row is not null) return row.Value;
        var id = conn.ExecuteScalar<long>(
            """
            INSERT INTO ingested_files(path, session_id, agent_id) VALUES(@path, @sessionId, @agentId)
            RETURNING id
            """, new { path, sessionId, agentId });
        return (id, 0L);
    }

    public void MarkFileStatus(SqliteConnection conn, long fileId, string status) =>
        conn.Execute("UPDATE ingested_files SET status=@status WHERE id=@fileId", new { fileId, status });

    /// <summary>Truncated/replaced-by-shorter file: drop everything it produced and start over.</summary>
    public void ResetFile(SqliteConnection conn, long fileId, string sessionId, string? agentId)
    {
        using var tx = conn.BeginTransaction();
        var p = new { sessionId, agentId };
        // One file maps to exactly one (session_id, agent_id) pair; parent-file rows have agent_id NULL.
        conn.Execute("DELETE FROM usage_events WHERE session_id=@sessionId AND agent_id IS @agentId", p, tx);
        conn.Execute("DELETE FROM tool_events WHERE session_id=@sessionId AND agent_id IS @agentId", p, tx);
        conn.Execute("DELETE FROM skill_invocations WHERE session_id=@sessionId AND agent_id IS @agentId", p, tx);
        conn.Execute("DELETE FROM record_stats WHERE file_id=@fileId", new { fileId }, tx);
        conn.Execute(
            "UPDATE ingested_files SET byte_offset=0, parse_errors=0, unknown_types=0 WHERE id=@fileId",
            new { fileId }, tx);
        tx.Commit();
    }

    public ApplyResult ApplyBatch(
        SqliteConnection conn, FileContext ctx, ParsedBatch batch,
        long newOffset, long fileSize, DateTimeOffset fileLastWriteUtc, CostCalculator costs)
    {
        var projectIds = new HashSet<long>();
        var sessionIds = new HashSet<string>();
        string? minDay = null;
        var newUnknownModels = false;

        using var tx = conn.BeginTransaction();

        // Sessions first (first-seen cwd wins for the session row).
        foreach (var s in batch.Sessions.Values)
        {
            long? pid = s.Cwd is not null ? ResolveProject(conn, tx, s.Cwd, s.TsUtc) : null;
            conn.Execute(
                """
                INSERT INTO sessions(id, project_id, git_branch, cli_version, first_ts_utc, last_ts_utc, title)
                VALUES(@Id, @Pid, @Branch, @Ver, @Ts, @Ts, @Title)
                ON CONFLICT(id) DO UPDATE SET
                  project_id   = COALESCE(sessions.project_id, excluded.project_id),
                  git_branch   = COALESCE(excluded.git_branch, sessions.git_branch),
                  cli_version  = COALESCE(excluded.cli_version, sessions.cli_version),
                  first_ts_utc = CASE WHEN excluded.first_ts_utc IS NOT NULL
                                       AND (sessions.first_ts_utc IS NULL OR excluded.first_ts_utc < sessions.first_ts_utc)
                                      THEN excluded.first_ts_utc ELSE sessions.first_ts_utc END,
                  last_ts_utc  = CASE WHEN excluded.last_ts_utc IS NOT NULL
                                       AND (sessions.last_ts_utc IS NULL OR excluded.last_ts_utc > sessions.last_ts_utc)
                                      THEN excluded.last_ts_utc ELSE sessions.last_ts_utc END,
                  title        = COALESCE(excluded.title, sessions.title)
                """,
                new
                {
                    s.SessionId, Id = s.SessionId, Pid = pid, Branch = s.GitBranch, Ver = s.CliVersion,
                    Ts = s.TsUtc?.ToString("O"), s.Title,
                }, tx);
            sessionIds.Add(s.SessionId);
        }

        foreach (var u in batch.UsageRows)
        {
            var pid = ResolveProjectForRow(conn, tx, u.Cwd, u.SessionId, u.TsUtc);
            projectIds.Add(pid);
            sessionIds.Add(u.SessionId);
            var dayLocal = TimeBuckets.DayLocal(u.TsUtc);
            if (minDay is null || string.CompareOrdinal(dayLocal, minDay) < 0) minDay = dayLocal;

            var (cost, savings) = costs.Compute(u);
            if (cost is null) newUnknownModels = true;

            conn.Execute(
                """
                INSERT INTO usage_events(
                  message_id, session_id, agent_id, project_id, ts_utc, day_local, model,
                  input_tokens, output_tokens, cache_creation, cache_read, cache_5m, cache_1h,
                  web_search, web_fetch, service_tier, cost_usd, cache_savings_usd,
                  attribution_skill, request_id, effort)
                VALUES(
                  @MessageId, @SessionId, @AgentId, @Pid, @Ts, @DayLocal, @Model,
                  @InputTokens, @OutputTokens, @CacheCreation, @CacheRead, @Cache5m, @Cache1h,
                  @WebSearch, @WebFetch, @ServiceTier, @Cost, @Savings,
                  @AttributionSkill, @RequestId, @Effort)
                ON CONFLICT(message_id) DO UPDATE SET
                  session_id=excluded.session_id, agent_id=excluded.agent_id, project_id=excluded.project_id,
                  ts_utc=excluded.ts_utc, day_local=excluded.day_local, model=excluded.model,
                  input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens,
                  cache_creation=excluded.cache_creation, cache_read=excluded.cache_read,
                  cache_5m=excluded.cache_5m, cache_1h=excluded.cache_1h,
                  web_search=excluded.web_search, web_fetch=excluded.web_fetch,
                  service_tier=excluded.service_tier, cost_usd=excluded.cost_usd,
                  cache_savings_usd=excluded.cache_savings_usd, attribution_skill=excluded.attribution_skill,
                  request_id=excluded.request_id, effort=excluded.effort
                """,
                new
                {
                    u.MessageId, u.SessionId, u.AgentId, Pid = pid, Ts = u.TsUtc.ToString("O"),
                    DayLocal = dayLocal, u.Model, u.InputTokens, u.OutputTokens, u.CacheCreation,
                    u.CacheRead, u.Cache5m, u.Cache1h, u.WebSearch, u.WebFetch, u.ServiceTier,
                    Cost = cost, Savings = savings, u.AttributionSkill, u.RequestId, u.Effort,
                }, tx);
        }

        // Two-phase tool events: INSERT on tool_use…
        foreach (var t in batch.ToolUses)
        {
            var pid = ResolveProjectForRow(conn, tx, t.Cwd, t.SessionId, t.TsUtc);
            conn.Execute(
                """
                INSERT INTO tool_events(tool_use_id, session_id, agent_id, project_id, ts_utc, day_local,
                                        tool_name, is_mcp, is_git_commit)
                VALUES(@ToolUseId, @SessionId, @AgentId, @Pid, @Ts, @DayLocal, @ToolName, @IsMcp, @IsGitCommit)
                ON CONFLICT(tool_use_id) DO NOTHING
                """,
                new
                {
                    t.ToolUseId, t.SessionId, t.AgentId, Pid = pid, Ts = t.TsUtc.ToString("O"),
                    DayLocal = TimeBuckets.DayLocal(t.TsUtc), t.ToolName, t.IsMcp, t.IsGitCommit,
                }, tx);
        }

        // …UPDATE on tool_result (crash-safe: no in-memory pending map).
        foreach (var r in batch.ToolResults)
        {
            conn.Execute(
                """
                UPDATE tool_events SET
                  is_error = @IsError,
                  lines_added = COALESCE(@LinesAdded, lines_added),
                  lines_removed = COALESCE(@LinesRemoved, lines_removed)
                WHERE tool_use_id = @ToolUseId
                """, r, tx);
        }

        foreach (var sk in batch.Skills)
        {
            var pid = ResolveProjectForRow(conn, tx, sk.Cwd, sk.SessionId, sk.TsUtc);
            conn.Execute(
                """
                INSERT OR IGNORE INTO skill_invocations(
                  record_uuid, session_id, project_id, ts_utc, day_local, skill_name, shape, agent_id, args)
                VALUES(@RecordUuid, @SessionId, @Pid, @Ts, @DayLocal, @SkillName, @Shape, @AgentId, @Args)
                """,
                new
                {
                    sk.RecordUuid, sk.SessionId, Pid = pid, Ts = sk.TsUtc.ToString("O"),
                    DayLocal = TimeBuckets.DayLocal(sk.TsUtc), sk.SkillName, sk.Shape, sk.AgentId, sk.Args,
                }, tx);
        }

        foreach (var (agentId, skillName) in batch.ForkedAgents)
        {
            conn.Execute(
                """
                INSERT INTO agents(agent_id, session_id, skill_name)
                VALUES(@agentId, @sessionId, @skillName)
                ON CONFLICT(agent_id) DO UPDATE SET
                  skill_name = COALESCE(excluded.skill_name, agents.skill_name),
                  session_id = COALESCE(agents.session_id, excluded.session_id)
                """, new { agentId, sessionId = ctx.SessionIdFromPath, skillName }, tx);
        }

        foreach (var (type, cnt) in batch.RecordTypeCounts)
        {
            conn.Execute(
                """
                INSERT INTO record_stats(file_id, record_type, cnt) VALUES(@FileId, @Type, @Cnt)
                ON CONFLICT(file_id, record_type) DO UPDATE SET cnt = record_stats.cnt + excluded.cnt
                """, new { ctx.FileId, Type = type, Cnt = cnt }, tx);
        }

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        foreach (var err in batch.Errors)
        {
            conn.Execute(
                "INSERT INTO parse_error_log(file, line_no, ts_utc, snippet) VALUES(@File, @LineNo, @Ts, @Snippet)",
                new { File = ctx.FilePath, LineNo = err.LineIndex, Ts = nowIso, err.Snippet }, tx);
        }
        if (batch.Errors.Count > 0)
        {
            conn.Execute(
                "DELETE FROM parse_error_log WHERE id NOT IN (SELECT id FROM parse_error_log ORDER BY id DESC LIMIT 200)",
                transaction: tx);
        }

        // The offset commits atomically with everything above.
        conn.Execute(
            """
            UPDATE ingested_files SET
              byte_offset=@newOffset, file_size=@fileSize, last_write_utc=@lastWrite, status='active',
              parse_errors = parse_errors + @pe, unknown_types = unknown_types + @ut
            WHERE id=@fileId
            """,
            new
            {
                newOffset, fileSize, lastWrite = fileLastWriteUtc.ToString("O"),
                pe = batch.Errors.Count, ut = batch.UnknownTypeCount, fileId = ctx.FileId,
            }, tx);

        tx.Commit();
        return new ApplyResult(projectIds, sessionIds, minDay, newUnknownModels, batch.Errors.Count);
    }

    public void UpsertAgentSidecars(
        SqliteConnection conn, string agentId, string sessionId, string? workflowId, AgentSidecars sidecars)
    {
        conn.Execute(
            """
            INSERT INTO agents(agent_id, session_id, parent_agent_id, agent_type, description, spawn_depth,
                               tool_use_id, workflow_id, skill_name, skill_effort, meta_loaded)
            VALUES(@agentId, @sessionId, @parentAgentId, @agentType, @description, @spawnDepth,
                   @toolUseId, @workflowId, @skillName, @skillEffort, @metaLoaded)
            ON CONFLICT(agent_id) DO UPDATE SET
              session_id      = COALESCE(agents.session_id, excluded.session_id),
              parent_agent_id = COALESCE(excluded.parent_agent_id, agents.parent_agent_id),
              agent_type      = COALESCE(excluded.agent_type, agents.agent_type),
              description     = COALESCE(excluded.description, agents.description),
              spawn_depth     = COALESCE(excluded.spawn_depth, agents.spawn_depth),
              tool_use_id     = COALESCE(excluded.tool_use_id, agents.tool_use_id),
              workflow_id     = COALESCE(excluded.workflow_id, agents.workflow_id),
              skill_name      = COALESCE(excluded.skill_name, agents.skill_name),
              skill_effort    = COALESCE(excluded.skill_effort, agents.skill_effort),
              meta_loaded     = MAX(agents.meta_loaded, excluded.meta_loaded)
            """,
            new
            {
                agentId, sessionId,
                parentAgentId = sidecars.Meta?.ParentAgentId,
                agentType = sidecars.Meta?.AgentType,
                description = sidecars.Meta?.Description,
                spawnDepth = sidecars.Meta?.SpawnDepth,
                toolUseId = sidecars.Meta?.ToolUseId,
                workflowId,
                skillName = sidecars.ForkedSkill?.SkillName is { } n ? SkillExtractor.NormalizeName(n) : null,
                skillEffort = sidecars.ForkedSkill?.Effort,
                metaLoaded = sidecars.Meta is not null ? 1 : 0,
            });
    }

    public void SeedPricingIfEmpty(SqliteConnection conn, IEnumerable<PricingRow> seed)
    {
        var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pricing");
        if (count > 0) return;
        foreach (var p in seed)
        {
            conn.Execute(
                """
                INSERT OR IGNORE INTO pricing(model_pattern, in_usd, out_usd, cache_w5m_usd, cache_w1h_usd, cache_r_usd)
                VALUES(@ModelPattern, @InUsd, @OutUsd, @CacheW5mUsd, @CacheW1hUsd, @CacheRUsd)
                """, p);
        }
    }

    public List<PricingRow> LoadPricing(SqliteConnection conn) =>
        conn.Query<PricingRow>(
            """
            SELECT model_pattern AS ModelPattern, in_usd AS InUsd, out_usd AS OutUsd,
                   cache_w5m_usd AS CacheW5mUsd, cache_w1h_usd AS CacheW1hUsd, cache_r_usd AS CacheRUsd
            FROM pricing
            """).ToList();

    /// <summary>Re-derives cost/savings on every usage row after a pricing edit; caller rebuilds blocks.</summary>
    public void RecomputeCosts(SqliteConnection conn, CostCalculator costs)
    {
        var rows = conn.Query<(string MessageId, string Model, long Input, long Output, long CacheCreation,
            long CacheRead, long Cache5m, long Cache1h)>(
            """
            SELECT message_id, model, input_tokens, output_tokens, cache_creation, cache_read, cache_5m, cache_1h
            FROM usage_events
            """).ToList();
        using var tx = conn.BeginTransaction();
        foreach (var r in rows)
        {
            var (cost, savings) = costs.Compute(
                r.Model, r.Input, r.Output, r.CacheCreation, r.CacheRead, r.Cache5m, r.Cache1h);
            conn.Execute(
                "UPDATE usage_events SET cost_usd=@cost, cache_savings_usd=@savings WHERE message_id=@id",
                new { cost, savings, id = r.MessageId }, tx);
        }
        tx.Commit();
    }

    private long ResolveProjectForRow(
        SqliteConnection conn, SqliteTransaction tx, string? cwd, string sessionId, DateTimeOffset ts)
    {
        if (cwd is not null) return ResolveProject(conn, tx, cwd, ts);
        var fromSession = conn.QuerySingleOrDefault<long?>(
            "SELECT project_id FROM sessions WHERE id=@sessionId", new { sessionId }, tx);
        return fromSession ?? ResolveProject(conn, tx, UnknownProjectCwd, ts);
    }

    private long ResolveProject(SqliteConnection conn, SqliteTransaction tx, string cwd, DateTimeOffset? ts)
    {
        if (_projectCache.TryGetValue(cwd, out var cached))
        {
            if (ts is not null)
            {
                conn.Execute(
                    """
                    UPDATE projects SET last_seen_utc = MAX(COALESCE(last_seen_utc,''), @ts) WHERE id=@cached
                    """, new { ts = ts.Value.ToString("O"), cached }, tx);
            }
            return cached;
        }

        var slug = cwd.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? cwd;
        var tsIso = (ts ?? DateTimeOffset.UtcNow).ToString("O");
        var id = conn.ExecuteScalar<long>(
            """
            INSERT INTO projects(cwd, slug, first_seen_utc, last_seen_utc) VALUES(@cwd, @slug, @tsIso, @tsIso)
            ON CONFLICT(cwd) DO UPDATE SET last_seen_utc = MAX(COALESCE(projects.last_seen_utc,''), excluded.last_seen_utc)
            RETURNING id
            """, new { cwd, slug, tsIso }, tx);
        _projectCache[cwd] = id;
        return id;
    }
}
