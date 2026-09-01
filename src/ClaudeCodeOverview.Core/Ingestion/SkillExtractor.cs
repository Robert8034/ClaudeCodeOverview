using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// Skill invocations never appear as a "Skill" tool_use in transcripts. Observed record shapes:
///  1. in_session   — user record, isMeta+turnCompanion, string content with &lt;skill-format&gt;true&lt;/skill-format&gt;
///  2. local_command — system record, subtype "local_command" (built-in slash commands like /model)
///  3. forked        — system record containing &lt;forked-skill-launch&gt;{json}&lt;/forked-skill-launch&gt;
///  4. marker-only   — user record whose ENTIRE content is command marker tags, with no isMeta,
///                     no turnCompanion and no skill-format marker. This is what CLI 2.1.220–2.1.234
///                     emit for both skills and built-in slash commands; shapes 1 and 2 catch only a
///                     fraction of invocations on those versions (verified against skillUsage in
///                     ~/.claude.json, 2026-09-01). See IMPLEMENTATION_PLAN.md §2.3.
///
/// Shape 4 carries no marker distinguishing a skill from a built-in command, so classification falls
/// back to <see cref="BuiltInCommands"/>: on the list means built-in, anything else counts as a skill.
/// A wrong entry only moves a row between the scorecard and the built-in table; no total changes.
/// </summary>
public static partial class SkillExtractor
{
    /// <summary>
    /// Claude Code's own built-in slash commands — the ones it does NOT record in the `skillUsage`
    /// counters of ~/.claude.json. Deliberately excludes init/doctor/statusline/review, which that
    /// file does count as skills. Add to this list when Anthropic ships new built-ins.
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltInCommands =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add-dir", "agents", "bashes", "bug", "clear", "compact", "config", "context", "cost",
            "effort", "exit", "export", "fast", "feedback", "help", "hooks", "ide",
            "install-github-app", "login", "logout", "mcp", "memory", "migrate-installer", "model",
            "output-style", "permissions", "pr-comments", "privacy-settings", "release-notes",
            "resume", "rewind", "sandbox", "status", "terminal-setup", "theme", "todos", "upgrade",
            "usage", "vim",
        };

    /// <summary>local_command = a built-in slash command; in_session = a real skill (§9 scorecard).</summary>
    public static string ClassifyShape(string name) =>
        BuiltInCommands.Contains(NormalizeName(name)) ? "local_command" : "in_session";

    [GeneratedRegex("<command-name>([^<]+)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex("<command-args>([^<]*)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgsRegex();

    [GeneratedRegex("<forked-skill-launch>(\\{.*?\\})</forked-skill-launch>", RegexOptions.Singleline)]
    private static partial Regex ForkedLaunchRegex();

    /// <summary>
    /// Wrappers whose CONTENT is echoed output or boilerplate, not an invocation. Removed first,
    /// nested payload and all: a &lt;command-name&gt; quoted *inside* a stdout echo describes a command
    /// that already ran, and counting it would double every built-in invocation.
    /// </summary>
    [GeneratedRegex("<(local-command-stdout|local-command-caveat)>.*?</\\1>", RegexOptions.Singleline)]
    private static partial Regex WrapperTagRegex();

    /// <summary>The tags that constitute an invocation record.</summary>
    [GeneratedRegex("<(command-name|command-message|command-args|skill-format)>.*?</\\1>",
        RegexOptions.Singleline)]
    private static partial Regex CommandTagRegex();

    public const string SkillFormatMarker = "<skill-format>true</skill-format>";

    /// <summary>Skill names are stored without a leading slash so the same skill groups across shapes.</summary>
    public static string NormalizeName(string name) => name.Trim().TrimStart('/');

    public static bool TryExtractInSession(TranscriptRecord rec, out string skillName, out string? args)
    {
        skillName = string.Empty;
        args = null;
        if (rec.IsMeta != true || rec.TurnCompanion != true) return false;
        // On user records the payload lives in message.content (string), not top-level content.
        var el = rec.Message?.Content ?? default;
        if (el.ValueKind != JsonValueKind.String) return false;
        var content = el.GetString();
        if (content is null || !content.Contains(SkillFormatMarker, StringComparison.Ordinal)) return false;
        var m = CommandNameRegex().Match(content);
        if (!m.Success) return false;
        skillName = NormalizeName(m.Groups[1].Value);
        var a = CommandArgsRegex().Match(content);
        args = a.Success && a.Groups[1].Value.Length > 0 ? a.Groups[1].Value : null;
        return skillName.Length > 0;
    }

    /// <summary>
    /// Shape 4. Matches only when the whole content is command markers — the strictness is the
    /// point: a "content contains &lt;command-name&gt;" rule reports every file edit and tool result
    /// that happens to quote these tags as an invocation, and this repo's own IMPLEMENTATION_PLAN.md
    /// documents them, so editing it would manufacture phantom skill usage.
    /// </summary>
    public static bool TryExtractMarkerOnlyCommand(TranscriptRecord rec, out string commandName, out string? args)
    {
        commandName = string.Empty;
        args = null;
        var el = rec.Message?.Content ?? default;
        if (el.ValueKind != JsonValueKind.String) return false;
        var content = el.GetString();
        if (content is null || content.Length == 0) return false;

        // Drop echoed output first, so a command name nested inside it cannot pose as an invocation.
        var payload = WrapperTagRegex().Replace(content, string.Empty);
        var m = CommandNameRegex().Match(payload);
        if (!m.Success) return false;
        // Everything outside the invocation tags must be whitespace.
        if (!string.IsNullOrWhiteSpace(CommandTagRegex().Replace(payload, string.Empty))) return false;

        commandName = NormalizeName(m.Groups[1].Value);
        var a = CommandArgsRegex().Match(payload);
        args = a.Success && a.Groups[1].Value.Length > 0 ? a.Groups[1].Value : null;
        return commandName.Length > 0;
    }

    public static bool TryExtractLocalCommand(TranscriptRecord rec, out string commandName, out string? args)
    {
        commandName = string.Empty;
        args = null;
        if (!string.Equals(rec.Subtype, "local_command", StringComparison.Ordinal)) return false;
        if (rec.Content.ValueKind != JsonValueKind.String) return false;
        var content = rec.Content.GetString();
        if (content is null) return false;
        var m = CommandNameRegex().Match(content);
        if (!m.Success) return false;
        commandName = NormalizeName(m.Groups[1].Value);
        var a = CommandArgsRegex().Match(content);
        args = a.Success && a.Groups[1].Value.Length > 0 ? a.Groups[1].Value : null;
        return commandName.Length > 0;
    }

    public static bool TryExtractForkedLaunch(TranscriptRecord rec, out ForkedSkillLaunch launch)
    {
        launch = null!;
        if (rec.Content.ValueKind != JsonValueKind.String) return false;
        var content = rec.Content.GetString();
        if (content is null || !content.Contains("<forked-skill-launch>", StringComparison.Ordinal)) return false;
        var m = ForkedLaunchRegex().Match(content);
        if (!m.Success) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize(m.Groups[1].Value, TranscriptJsonContext.Default.ForkedSkillLaunch);
            if (parsed?.SkillName is null) return false;
            launch = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
