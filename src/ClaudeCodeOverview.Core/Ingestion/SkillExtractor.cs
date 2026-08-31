using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// Skill invocations never appear as a "Skill" tool_use in transcripts. They come in three shapes:
///  1. in_session   — user record, isMeta+turnCompanion, string content with &lt;skill-format&gt;true&lt;/skill-format&gt;
///  2. local_command — system record, subtype "local_command" (built-in slash commands like /model)
///  3. forked        — system record containing &lt;forked-skill-launch&gt;{json}&lt;/forked-skill-launch&gt;
/// </summary>
public static partial class SkillExtractor
{
    [GeneratedRegex("<command-name>([^<]+)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex("<command-args>([^<]*)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgsRegex();

    [GeneratedRegex("<forked-skill-launch>(\\{.*?\\})</forked-skill-launch>", RegexOptions.Singleline)]
    private static partial Regex ForkedLaunchRegex();

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
