using ClaudeCodeOverview.Core.Ingestion;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeOverview.Core.Data;

/// <summary>
/// One-time data fixes that cannot be expressed as a schema migration because they depend on
/// re-reading transcripts or on C# logic. Each runs once, guarded by a key in <c>settings</c>.
/// Called by the ingestion service (the single writer) right after <see cref="Migrator"/>.
/// </summary>
public static class DataUpgrades
{
    public const string SkillShapeKey = "upgrade:skill_shapes_v2";

    /// <summary>
    /// Skill detection gained the marker-only record shape and a built-in-command list
    /// (IMPLEMENTATION_PLAN.md §2.3 DRIFT). Existing databases need two things the parser change
    /// alone cannot deliver:
    ///
    ///  1. Rows already stored carry the old classification, and <c>skill_invocations</c> inserts are
    ///     INSERT OR IGNORE on record_uuid — a re-parse would keep the stale shape. So reclassify in
    ///     place. This also covers history whose transcripts Claude Code has since deleted, which is
    ///     exactly the history this database exists to preserve.
    ///  2. Invocations that were never captured sit in bytes the tailer has already consumed, and
    ///     <c>FindChangedFiles</c> skips any file whose size equals its stored offset. So rewind the
    ///     files that are still on disk and let the normal backfill re-read them. Re-ingestion is
    ///     idempotent (usage_events upserts last-wins, tool_events conflict-ignores), and
    ///     <see cref="IngestRepository.ResetFile"/> clears the per-file rows that would otherwise
    ///     double-count.
    ///
    /// Files that have vanished are left alone: their rows are the only remaining record of them.
    /// </summary>
    public static void RunSkillShapeUpgrade(
        SqliteConnection conn, IngestRepository repo, ILogger logger, Func<string, bool>? fileExists = null)
    {
        var already = conn.ExecuteScalar<string?>(
            "SELECT value FROM settings WHERE key = @key", new { key = SkillShapeKey });
        if (already is not null) return;

        fileExists ??= File.Exists;

        var reclassified = 0;
        var stored = conn.Query<(long Id, string Name, string Shape)>(
            "SELECT id, skill_name, shape FROM skill_invocations WHERE shape <> 'forked'").ToList();
        foreach (var (id, name, shape) in stored)
        {
            var want = SkillExtractor.ClassifyShape(name);
            if (want == shape) continue;
            conn.Execute("UPDATE skill_invocations SET shape = @want WHERE id = @id", new { want, id });
            reclassified++;
        }

        var rewound = 0;
        var files = conn.Query<(long Id, string Path, string? SessionId, string? AgentId)>(
            "SELECT id, path, session_id, agent_id FROM ingested_files WHERE status = 'active'").ToList();
        foreach (var (id, path, sessionId, agentId) in files)
        {
            if (sessionId is null || !fileExists(path)) continue;
            repo.ResetFile(conn, id, sessionId, agentId);
            rewound++;
        }

        conn.Execute(
            """
            INSERT INTO settings(key, value) VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            new { key = SkillShapeKey, value = DateTimeOffset.UtcNow.ToString("O") });

        if (reclassified > 0 || rewound > 0)
        {
            logger.LogInformation(
                "Skill-shape upgrade: reclassified {Reclassified} stored invocations, " +
                "rewound {Rewound} transcripts for re-ingestion", reclassified, rewound);
        }
    }
}
