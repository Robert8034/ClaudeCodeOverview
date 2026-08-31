namespace ClaudeCodeOverview.Core.Ingestion;

public sealed record TranscriptPathInfo(string SessionId, string? AgentId, string? WorkflowId);

/// <summary>
/// Classifies paths under the transcript root:
///   &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;.jsonl                                  → parent session
///   &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.jsonl             → subagent
///   &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;/subagents/workflows/wf_&lt;id&gt;/agent-*.jsonl → workflow agent
/// </summary>
public static class TranscriptPaths
{
    public static bool IsIngestible(string path)
    {
        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("journal.jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.StartsWith(".syncthing.", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Contains("~syncthing~", StringComparison.OrdinalIgnoreCase)) return false;

        var norm = path.Replace('/', '\\');
        return !ContainsSegment(norm, "tool-results")
            && !norm.Contains("\\workflows\\scripts\\", StringComparison.OrdinalIgnoreCase)
            && !ContainsSegment(norm, "memory")
            && !ContainsSegment(norm, ".stfolder")
            && !ContainsSegment(norm, ".stversions");
    }

    private static bool ContainsSegment(string normalizedPath, string segment) =>
        normalizedPath.Contains($"\\{segment}\\", StringComparison.OrdinalIgnoreCase);

    public static TranscriptPathInfo Classify(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!fileName.StartsWith("agent-", StringComparison.Ordinal))
            return new TranscriptPathInfo(fileName, null, null);

        var agentId = fileName["agent-".Length..];
        var segments = path.Replace('/', '\\').Split('\\');
        var subagentsIdx = Array.FindLastIndex(segments,
            s => s.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var sessionId = subagentsIdx > 0 ? segments[subagentsIdx - 1] : "(unknown-session)";
        string? workflowId = null;
        if (subagentsIdx >= 0)
        {
            for (var i = subagentsIdx + 1; i < segments.Length - 1; i++)
            {
                if (segments[i].StartsWith("wf_", StringComparison.Ordinal))
                {
                    workflowId = segments[i];
                    break;
                }
            }
        }
        return new TranscriptPathInfo(sessionId, agentId, workflowId);
    }
}
