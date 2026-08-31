using System.Text.Json;

namespace ClaudeCodeOverview.Core.Ingestion;

public sealed record AgentSidecars(AgentMetaFile? Meta, ForkedSkillFile? ForkedSkill, bool AllPresentOrAbsent);

/// <summary>
/// Reads the sidecar files next to a subagent transcript (agent-&lt;id&gt;.jsonl):
/// agent-&lt;id&gt;.meta.json and optional agent-&lt;id&gt;.forked-skill.json.
/// Sidecars can arrive after the JSONL (Syncthing ordering) — callers retry on rescan
/// while an agents row is still incomplete.
/// </summary>
public static class AgentMetaReader
{
    public static AgentSidecars Read(string agentJsonlPath)
    {
        var basePath = agentJsonlPath[..^".jsonl".Length];
        var meta = TryRead(basePath + ".meta.json", TranscriptJsonContext.Default.AgentMetaFile);
        var forked = TryRead(basePath + ".forked-skill.json", TranscriptJsonContext.Default.ForkedSkillFile);
        // meta.json is expected for every agent; forked-skill.json only for forked skills.
        var complete = meta is not null;
        return new AgentSidecars(meta, forked, complete);
    }

    private static T? TryRead<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, typeInfo);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
