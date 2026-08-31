using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// Tool calls are two-phase in transcripts: the assistant record carries tool_use blocks,
/// the following user record carries the matching tool_result (+ toolUseResult detail).
/// Ingestion mirrors that: INSERT on tool_use, UPDATE on tool_result — crash-safe because
/// no pending state lives outside the database.
/// </summary>
public static partial class ToolEventExtractor
{
    [GeneratedRegex(@"\bgit\b[\s\S]*?\bcommit\b")]
    private static partial Regex GitCommitRegex();

    public static void ExtractToolUses(
        JsonElement messageContent, string sessionId, string? agentId, string? cwd,
        DateTimeOffset tsUtc, List<ToolUseRow> sink)
    {
        if (messageContent.ValueKind != JsonValueKind.Array) return;
        foreach (var block in messageContent.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(block, "type", out var type) || type != "tool_use") continue;
            if (!TryGetString(block, "id", out var id) || !TryGetString(block, "name", out var name)) continue;

            var isMcp = name.StartsWith("mcp__", StringComparison.Ordinal);
            var isGitCommit = false;
            if (name is "Bash" or "PowerShell"
                && block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                && TryGetString(input, "command", out var command))
            {
                isGitCommit = GitCommitRegex().IsMatch(command);
            }

            sink.Add(new ToolUseRow(id, sessionId, agentId, cwd, tsUtc, name, isMcp, isGitCommit));
        }
    }

    public static void ExtractToolResults(TranscriptRecord rec, List<ToolResultRow> sink)
    {
        // On user records the tool_result blocks live in message.content; toolUseResult is top-level.
        var content = rec.Message?.Content ?? default;
        if (content.ValueKind != JsonValueKind.Array) return;

        var results = new List<(string Id, bool IsError)>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetString(block, "type", out var type) || type != "tool_result") continue;
            if (!TryGetString(block, "tool_use_id", out var toolUseId)) continue;
            var isError = block.TryGetProperty("is_error", out var err)
                          && err.ValueKind == JsonValueKind.True;
            results.Add((toolUseId, isError));
        }
        if (results.Count == 0) return;

        // toolUseResult describes the single tool result of this record; only attach line
        // counts when the mapping is unambiguous. Never guess from tool inputs.
        (int Added, int Removed)? lines = results.Count == 1 ? CountPatchLines(rec.ToolUseResult) : null;

        for (var i = 0; i < results.Count; i++)
        {
            var (id, isError) = results[i];
            sink.Add(new ToolResultRow(id, isError, lines?.Added, lines?.Removed));
        }
    }

    /// <summary>
    /// Counts +/- lines from a structuredPatch array if the toolUseResult carries one
    /// (Edit-style results). Returns null when no structured patch data exists.
    /// </summary>
    internal static (int Added, int Removed)? CountPatchLines(JsonElement toolUseResult)
    {
        if (toolUseResult.ValueKind != JsonValueKind.Object) return null;
        if (!toolUseResult.TryGetProperty("structuredPatch", out var patches)
            || patches.ValueKind != JsonValueKind.Array) return null;

        int added = 0, removed = 0;
        var sawAny = false;
        foreach (var patch in patches.EnumerateArray())
        {
            if (patch.ValueKind != JsonValueKind.Object) continue;
            if (!patch.TryGetProperty("lines", out var patchLines) || patchLines.ValueKind != JsonValueKind.Array) continue;
            foreach (var line in patchLines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String) continue;
                var s = line.GetString();
                if (s is null || s.Length == 0) continue;
                sawAny = true;
                if (s[0] == '+') added++;
                else if (s[0] == '-') removed++;
            }
        }
        return sawAny ? (added, removed) : null;
    }

    private static bool TryGetString(JsonElement obj, string property, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var s = el.GetString();
        if (s is null) return false;
        value = s;
        return true;
    }
}
