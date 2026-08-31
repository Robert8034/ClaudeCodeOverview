using System.Text.Json;
using ClaudeCodeOverview.Core.Data;
using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Ingestion;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;

namespace ClaudeCodeOverview.Tests;

/// <summary>
/// Full pipeline over a complete sanitized REAL session, checked against ground-truth totals
/// computed independently (last-wins per message.id, top-level usage only).
/// </summary>
public class IntegrationTests
{
    private sealed record Expected(
        int Lines, int AssistantLines, int DedupedMessages,
        long InputTokens, long OutputTokens, long CacheCreation, long CacheRead,
        string[] Models, int ToolUseBlocks, Dictionary<string, int> SkillInvocations);

    private static Expected LoadExpected()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixtures.PathOf("session_small.expected.json")));
        var r = doc.RootElement;
        return new Expected(
            r.GetProperty("lines").GetInt32(),
            r.GetProperty("assistantLines").GetInt32(),
            r.GetProperty("dedupedMessages").GetInt32(),
            r.GetProperty("inputTokens").GetInt64(),
            r.GetProperty("outputTokens").GetInt64(),
            r.GetProperty("cacheCreation").GetInt64(),
            r.GetProperty("cacheRead").GetInt64(),
            r.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray(),
            r.GetProperty("toolUseBlocks").GetInt32(),
            r.GetProperty("skillInvocations").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetInt32()));
    }

    private static void IngestFixtureSession(TestDb db, IngestRepository repo)
    {
        var costs = new CostCalculator(CostCalculator.DefaultSeed);
        var path = Fixtures.PathOf("session_small.jsonl");
        var info = TranscriptPaths.Classify(path);
        var (fileId, offset) = repo.EnsureFile(db.Connection, path, info.SessionId, info.AgentId);
        var tail = FileTailer.ReadNewLines(path, offset);
        var ctx = new FileContext(fileId, path, info.SessionId, info.AgentId, info.WorkflowId, null,
            DateTimeOffset.UtcNow);
        var batch = TranscriptLineParser.Parse(ctx, tail.Lines);
        Assert.Empty(batch.Errors);
        repo.ApplyBatch(db.Connection, ctx, batch, tail.NewOffset, new FileInfo(path).Length,
            DateTimeOffset.UtcNow, costs);
        BlockCalculator.RebuildAll(db.Connection);
    }

    [Fact]
    public void Full_session_matches_independently_computed_totals()
    {
        var expected = LoadExpected();
        using var db = new TestDb();
        var repo = new IngestRepository();

        IngestFixtureSession(db, repo);

        var totals = db.Connection.QuerySingle<(long Msgs, long Input, long Output, long CacheW, long CacheR)>(
            """
            SELECT COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(cache_creation), SUM(cache_read)
            FROM usage_events
            """);
        Assert.Equal(expected.DedupedMessages, totals.Msgs);
        Assert.Equal(expected.InputTokens, totals.Input);
        Assert.Equal(expected.OutputTokens, totals.Output);
        Assert.Equal(expected.CacheCreation, totals.CacheW);
        Assert.Equal(expected.CacheRead, totals.CacheR);

        var models = db.Connection.Query<string>("SELECT DISTINCT model FROM usage_events ORDER BY model").ToList();
        Assert.Equal(expected.Models.Order(), models);

        foreach (var (shape, count) in expected.SkillInvocations)
        {
            var actual = db.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM skill_invocations WHERE shape=@shape", new { shape });
            Assert.Equal(count, actual);
        }

        // tool_events dedups repeated tool_use blocks (streaming) on tool_use_id.
        var toolEvents = db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tool_events");
        Assert.True(toolEvents > 0 && toolEvents <= expected.ToolUseBlocks);

        // Costs are computed for the known model, and blocks exist.
        Assert.Equal(0, db.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM usage_events WHERE cost_usd IS NULL"));
        Assert.True(db.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_blocks") > 0);
    }

    [Fact]
    public void Reingesting_from_scratch_is_deterministic()
    {
        using var db1 = new TestDb();
        using var db2 = new TestDb();
        var repo1 = new IngestRepository();
        var repo2 = new IngestRepository();

        IngestFixtureSession(db1, repo1);
        IngestFixtureSession(db2, repo2);

        const string totalsSql =
            """
            SELECT COUNT(*) || '|' || SUM(input_tokens) || '|' || SUM(output_tokens) || '|' ||
                   SUM(cache_creation) || '|' || SUM(cache_read) || '|' || ROUND(SUM(cost_usd), 8)
            FROM usage_events
            """;
        Assert.Equal(
            db1.Connection.ExecuteScalar<string>(totalsSql),
            db2.Connection.ExecuteScalar<string>(totalsSql));
    }

    [Fact]
    public void Incremental_tailing_matches_single_pass_ingestion()
    {
        // Split the fixture file in half mid-line to prove offset-based resume is lossless.
        var source = File.ReadAllBytes(Fixtures.PathOf("session_small.jsonl"));
        var tmp = Path.Combine(Path.GetTempPath(), $"ccov-inc-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var db = new TestDb();
            var repo = new IngestRepository();
            var costs = new CostCalculator(CostCalculator.DefaultSeed);

            File.WriteAllBytes(tmp, source[..(source.Length / 2)]);
            var info = TranscriptPaths.Classify(tmp);
            var (fileId, _) = repo.EnsureFile(db.Connection, tmp, info.SessionId, info.AgentId);
            var ctx = new FileContext(fileId, tmp, info.SessionId, info.AgentId, null, null, DateTimeOffset.UtcNow);

            var tail1 = FileTailer.ReadNewLines(tmp, 0);
            repo.ApplyBatch(db.Connection, ctx, TranscriptLineParser.Parse(ctx, tail1.Lines),
                tail1.NewOffset, source.Length / 2, DateTimeOffset.UtcNow, costs);

            File.WriteAllBytes(tmp, source); // "append" the second half
            var tail2 = FileTailer.ReadNewLines(tmp, tail1.NewOffset);
            repo.ApplyBatch(db.Connection, ctx, TranscriptLineParser.Parse(ctx, tail2.Lines),
                tail2.NewOffset, source.Length, DateTimeOffset.UtcNow, costs);

            var expected = LoadExpected();
            var totals = db.Connection.QuerySingle<(long Msgs, long Output)>(
                "SELECT COUNT(*), SUM(output_tokens) FROM usage_events");
            Assert.Equal(expected.DedupedMessages, totals.Msgs);
            Assert.Equal(expected.OutputTokens, totals.Output);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
