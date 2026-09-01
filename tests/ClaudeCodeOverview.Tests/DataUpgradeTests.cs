using ClaudeCodeOverview.Core.Data;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeOverview.Tests;

/// <summary>
/// The skill-detection change only helps an EXISTING database if stored rows are reclassified and
/// already-consumed transcripts are re-read: skill_invocations inserts are INSERT OR IGNORE, and
/// FindChangedFiles skips any file whose size equals its stored byte offset.
/// </summary>
public class DataUpgradeTests
{
    private static long Offset(TestDb db, string like) => db.Connection.ExecuteScalar<long>(
        "SELECT byte_offset FROM ingested_files WHERE path LIKE @like", new { like });

    [Fact]
    public void Stored_invocations_are_reclassified_and_live_files_rewound()
    {
        using var db = new TestDb();
        var repo = new IngestRepository();
        Fixtures.Ingest(db, repo, "session_small.jsonl");

        // Simulate a database written before the change: everything filed as a built-in command.
        db.Connection.Execute("UPDATE skill_invocations SET shape = 'local_command'");
        db.Connection.Execute(
            """
            INSERT INTO skill_invocations(record_uuid, session_id, project_id, ts_utc, day_local,
                                          skill_name, shape)
            VALUES('uuid-init', 's-old', 1, '2026-08-01T10:00:00Z', '2026-08-01', 'init', 'local_command'),
                  ('uuid-hubspot', 's-old', 1, '2026-08-01T10:01:00Z', '2026-08-01', 'hubspot', 'local_command'),
                  ('uuid-forked', 's-old', 1, '2026-08-01T10:02:00Z', '2026-08-01', 'code-review', 'forked')
            """);
        var offsetBefore = Offset(db, "%session_small%");
        Assert.True(offsetBefore > 0, "the fixture file should be fully consumed");

        DataUpgrades.RunSkillShapeUpgrade(db.Connection, repo, NullLogger.Instance);

        // Real skills move to the scorecard; built-ins stay put; forked rows are never touched.
        // DISTINCT: a name may legitimately appear many times (session_small has three /model rows),
        // but after reclassification every name must map to exactly one shape.
        var shapes = db.Connection.Query<(string Name, string Shape)>(
            "SELECT DISTINCT skill_name, shape FROM skill_invocations").ToDictionary(r => r.Name, r => r.Shape);
        Assert.Equal("in_session", shapes["init"]);
        Assert.Equal("in_session", shapes["hubspot"]);
        Assert.Equal("forked", shapes["code-review"]);
        Assert.Equal("local_command", shapes["model"]);   // session_small's own /model records

        // The transcript is rewound so the backfill re-reads it and can pick up shapes the old
        // parser skipped; its derived rows are cleared so the re-read cannot double-count.
        Assert.Equal(0, Offset(db, "%session_small%"));
        Assert.Equal(0, db.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM usage_events WHERE session_id = (SELECT session_id FROM ingested_files LIMIT 1)"));
    }

    [Fact]
    public void History_whose_transcript_is_gone_keeps_its_rows()
    {
        using var db = new TestDb();
        var repo = new IngestRepository();
        Fixtures.Ingest(db, repo, "session_small.jsonl");
        var usageBefore = db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM usage_events");
        Assert.True(usageBefore > 0);

        // Claude Code deleted the source transcript (cleanupPeriodDays); this database is now the
        // only record of it. Rewinding would destroy that history.
        DataUpgrades.RunSkillShapeUpgrade(db.Connection, repo, NullLogger.Instance, _ => false);

        Assert.Equal(usageBefore, db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM usage_events"));
        Assert.True(Offset(db, "%session_small%") > 0, "a vanished file must not be rewound");
    }

    [Fact]
    public void The_upgrade_runs_only_once()
    {
        using var db = new TestDb();
        var repo = new IngestRepository();
        Fixtures.Ingest(db, repo, "session_small.jsonl");

        DataUpgrades.RunSkillShapeUpgrade(db.Connection, repo, NullLogger.Instance);
        Assert.Equal(0, Offset(db, "%session_small%"));

        // Re-ingest, then run again: a second pass must not rewind the file all over again.
        Fixtures.Ingest(db, repo, "session_small.jsonl");
        var offsetAfterReingest = Offset(db, "%session_small%");
        Assert.True(offsetAfterReingest > 0);

        DataUpgrades.RunSkillShapeUpgrade(db.Connection, repo, NullLogger.Instance);
        Assert.Equal(offsetAfterReingest, Offset(db, "%session_small%"));
    }
}
