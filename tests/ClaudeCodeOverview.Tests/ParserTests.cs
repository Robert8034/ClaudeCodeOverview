using ClaudeCodeOverview.Core.Data;
using ClaudeCodeOverview.Core.Ingestion;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;

namespace ClaudeCodeOverview.Tests;

/// <summary>Fixture-driven parser tests over sanitized REAL transcript records.</summary>
public class ParserTests
{
    private static FileContext Ctx(long fileId = 1, string? agentId = null, string? forkedSkill = null) =>
        new(fileId, "fixture.jsonl", "fixture-session", agentId, null, forkedSkill, DateTimeOffset.UtcNow);

    [Fact]
    public void Records_various_extracts_every_shape()
    {
        var batch = TranscriptLineParser.Parse(Ctx(), Fixtures.Lines("records_various.jsonl"));

        Assert.Empty(batch.Errors);

        // (a) iterations trap: only TOP-LEVEL usage counters are read.
        var first = batch.UsageRows[0];
        Assert.Equal(2, first.InputTokens);
        Assert.Equal(514, first.OutputTokens);
        Assert.Equal(9160, first.CacheCreation);
        Assert.Equal(26364, first.CacheRead);
        Assert.Equal(0, first.Cache5m);
        Assert.Equal(9160, first.Cache1h);

        // (b) subagent attribution comes from the record itself.
        var sub = batch.UsageRows[1];
        Assert.Equal("a340a8eced20e234f", sub.AgentId);
        Assert.Equal("code-review", sub.AttributionSkill);

        // (c) in-session skill — payload lives in message.content, not top-level content.
        var inSession = Assert.Single(batch.Skills, s => s.Shape == "in_session");
        Assert.Equal("workflow-authoring", inSession.SkillName);

        // (d) built-in slash command, name normalized without the leading slash.
        var local = Assert.Single(batch.Skills, s => s.Shape == "local_command");
        Assert.Equal("model", local.SkillName);
        Assert.Equal("fable5", local.Args);

        // (e) forked skill launch + agent mapping (record ALSO has subtype local_command).
        var forked = Assert.Single(batch.Skills, s => s.Shape == "forked");
        Assert.Equal("code-review", forked.SkillName);
        Assert.Equal("a9b516948c04b2f52", forked.AgentId);
        Assert.Equal("code-review", batch.ForkedAgents["a9b516948c04b2f52"]);

        // (f) other record types are counted, never thrown.
        Assert.Equal(1, batch.RecordTypeCounts["queue-operation"]);
        Assert.Equal(0, batch.UnknownTypeCount); // queue-operation is a known type

        // (g) ai-title lands on the session.
        Assert.Contains(batch.Sessions.Values,
            s => s.Title == "Review database switching implementation across APIs");
    }

    [Fact]
    public void Unknown_record_types_are_counted_not_thrown()
    {
        var lines = new List<byte[]>
        {
            System.Text.Encoding.UTF8.GetBytes("""{"type":"totally-new-type","sessionId":"x"}"""),
            System.Text.Encoding.UTF8.GetBytes("this is not json at all"),
        };
        var batch = TranscriptLineParser.Parse(Ctx(), lines);

        Assert.Equal(1, batch.UnknownTypeCount);
        Assert.Single(batch.Errors);
    }

    [Fact]
    public void Tool_pair_extracts_use_and_result_with_error_flag_and_patch_lines()
    {
        var batch = TranscriptLineParser.Parse(Ctx(), Fixtures.Lines("tool_pair.jsonl"));

        Assert.True(batch.ToolUses.Count >= 2);
        Assert.Contains(batch.ToolUses, t => t.ToolName == "Edit");

        // Every tool_result matches a tool_use from the preceding assistant record.
        var useIds = batch.ToolUses.Select(t => t.ToolUseId).ToHashSet();
        Assert.All(batch.ToolResults, r => Assert.Contains(r.ToolUseId, useIds));

        // Real error result (is_error is ABSENT on success, true on failure).
        Assert.Contains(batch.ToolResults, r => r.IsError);
        Assert.Contains(batch.ToolResults, r => !r.IsError);

        // The successful Edit carries structuredPatch → line counts; never guessed otherwise.
        Assert.Contains(batch.ToolResults, r => !r.IsError && r.LinesAdded is not null);
        Assert.All(batch.ToolResults.Where(r => r.IsError), r => Assert.Null(r.LinesAdded));
    }

    [Fact]
    public void Git_commit_detection_covers_bash_and_powershell_only()
    {
        var n = 0;
        var mk = (string tool, string cmd) =>
        {
            var id = $"{tool}-{++n}";
            var json =
                "{\"type\":\"assistant\",\"sessionId\":\"s\",\"timestamp\":\"2026-08-31T10:00:00Z\",\"cwd\":\"C:\\\\x\"," +
                "\"message\":{\"id\":\"m-" + id + "\",\"model\":\"claude-opus-5\"," +
                "\"usage\":{\"input_tokens\":1,\"output_tokens\":1}," +
                "\"content\":[{\"type\":\"tool_use\",\"id\":\"t-" + id + "\",\"name\":\"" + tool + "\"," +
                "\"input\":{\"command\":\"" + cmd + "\"}}]}}";
            return System.Text.Encoding.UTF8.GetBytes(json);
        };

        var batch = TranscriptLineParser.Parse(Ctx(), new List<byte[]>
        {
            mk("Bash", "git add -A && git commit -m x"),
            mk("PowerShell", "git commit -m done"),
            mk("Bash", "git status"),
            mk("Read", "git commit"),
        });

        Assert.Equal(2, batch.ToolUses.Count(t => t.IsGitCommit));
        Assert.DoesNotContain(batch.ToolUses, t => t.ToolName == "Read" && t.IsGitCommit);
    }

    [Fact]
    public void Repository_dedup_is_last_wins_on_message_id()
    {
        using var db = new TestDb();
        var repo = new IngestRepository();
        var costs = new CostCalculator(CostCalculator.DefaultSeed);
        var ctx = Ctx();
        var (fileId, _) = repo.EnsureFile(db.Connection, ctx.FilePath, ctx.SessionIdFromPath, null);
        ctx = ctx with { FileId = fileId };

        var batch = TranscriptLineParser.Parse(ctx, Fixtures.Lines("stream_repeat.jsonl"));
        Assert.Equal(2, batch.UsageRows.Count);
        Assert.Equal(batch.UsageRows[0].MessageId, batch.UsageRows[1].MessageId);
        Assert.Equal(10, batch.UsageRows[0].OutputTokens); // synthesized first streaming line

        repo.ApplyBatch(db.Connection, ctx, batch, 100, 100, DateTimeOffset.UtcNow, costs);

        var (count, output) = db.Connection.QuerySingle<(long, long)>(
            "SELECT COUNT(*), MAX(output_tokens) FROM usage_events");
        Assert.Equal(1, count);
        Assert.Equal(batch.UsageRows[^1].OutputTokens, output); // the FINAL line's counts win
        Assert.NotEqual(10, output);

        // Re-applying the same batch is idempotent.
        repo.ApplyBatch(db.Connection, ctx, batch, 100, 100, DateTimeOffset.UtcNow, costs);
        Assert.Equal(1, db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM usage_events"));
    }

    [Fact]
    public void Reset_file_removes_everything_it_produced()
    {
        using var db = new TestDb();
        var repo = new IngestRepository();
        var costs = new CostCalculator(CostCalculator.DefaultSeed);
        var (fileId, _) = repo.EnsureFile(db.Connection, "f.jsonl", "fixture-session", null);
        var ctx = Ctx(fileId);

        var batch = TranscriptLineParser.Parse(ctx, Fixtures.Lines("records_various.jsonl"));
        repo.ApplyBatch(db.Connection, ctx, batch, 10, 10, DateTimeOffset.UtcNow, costs);
        Assert.True(db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM record_stats WHERE file_id=@fileId", new { fileId }) > 0);

        repo.ResetFile(db.Connection, fileId, "fixture-session", null);

        Assert.Equal(0, db.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM usage_events WHERE session_id='fixture-session' AND agent_id IS NULL"));
        Assert.Equal(0, db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM record_stats WHERE file_id=@fileId", new { fileId }));
        Assert.Equal(0L, db.Connection.ExecuteScalar<long>("SELECT byte_offset FROM ingested_files WHERE id=@fileId", new { fileId }));
    }
}
