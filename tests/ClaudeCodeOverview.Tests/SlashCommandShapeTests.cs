using ClaudeCodeOverview.Core.Ingestion;

namespace ClaudeCodeOverview.Tests;

/// <summary>
/// CLI 2.1.220–2.1.234 emit slash commands and skills as a user record whose entire content is
/// command marker tags — no isMeta, no turnCompanion, no skill-format. Verified against real
/// transcripts on 2026-09-01, where the older rules caught 2 of 8 invocations.
///
/// The false-positive cases matter as much as the positive ones: these marker tags appear verbatim
/// inside IMPLEMENTATION_PLAN.md, so a "content contains &lt;command-name&gt;" rule would count this
/// repo's own file edits as skill usage.
/// </summary>
public class SlashCommandShapeTests
{
    private static ParsedBatch Parse(string fixture)
    {
        var ctx = new FileContext(1, Fixtures.PathOf(fixture), "cccccccc-0000-4000-8000-00000000000c",
            null, null, null, DateTimeOffset.UtcNow);
        return TranscriptLineParser.Parse(ctx, Fixtures.Lines(fixture));
    }

    [Fact]
    public void Marker_only_records_are_counted_and_classified()
    {
        var batch = Parse("marker_only_commands.jsonl");
        Assert.Empty(batch.Errors);

        var byName = batch.Skills.ToDictionary(s => s.SkillName);
        Assert.Equal(3, batch.Skills.Count);

        // Built-in commands go to the commands table, never the skills scorecard.
        Assert.Equal("local_command", byName["clear"].Shape);
        Assert.Equal("local_command", byName["model"].Shape);
        Assert.Equal("claude-opus-5", byName["model"].Args);

        // /init is a skill: Claude Code counts it in skillUsage, so it belongs on the scorecard.
        Assert.Equal("in_session", byName["init"].Shape);

        // Names are normalized without the leading slash so shapes group together.
        Assert.All(batch.Skills, s => Assert.False(s.SkillName.StartsWith('/')));
    }

    [Fact]
    public void Prose_and_tool_results_quoting_the_markers_are_not_invocations()
    {
        var batch = Parse("marker_only_commands.jsonl");

        // Fixture line 5 is a user message *describing* the tags; line 6 is a tool_result whose text
        // quotes them. Neither is an invocation — and neither name may leak into the results.
        Assert.DoesNotContain(batch.Skills, s => s.SkillName == "deploy");
        Assert.Equal(3, batch.Skills.Count);
    }

    [Fact]
    public void Stdout_echo_without_a_command_name_is_not_an_invocation()
    {
        var batch = Parse("marker_only_commands.jsonl");
        Assert.DoesNotContain(batch.Skills, s => s.SkillName.Contains("Login", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("clear", "local_command")]
    [InlineData("/model", "local_command")]
    [InlineData("resume", "local_command")]
    [InlineData("init", "in_session")]
    [InlineData("code-review", "in_session")]
    [InlineData("hubspot", "in_session")]
    public void Classification_uses_the_built_in_command_list(string name, string expectedShape) =>
        Assert.Equal(expectedShape, SkillExtractor.ClassifyShape(name));

    [Fact]
    public void The_old_skill_format_shape_still_wins_when_present()
    {
        // records_various.jsonl line c is the isMeta+turnCompanion+skill-format shape.
        var batch = Parse("records_various.jsonl");
        Assert.Contains(batch.Skills, s => s.Shape == "in_session");
    }
}
