using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// One line of a Claude Code transcript JSONL file. All fields are optional by design:
/// the format is unversioned and drifts across CLI versions, so absence must never throw.
/// </summary>
public sealed class TranscriptRecord
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("parentUuid")] public string? ParentUuid { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("gitBranch")] public string? GitBranch { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("isSidechain")] public bool? IsSidechain { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("effort")] public string? Effort { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("isMeta")] public bool? IsMeta { get; set; }
    [JsonPropertyName("turnCompanion")] public bool? TurnCompanion { get; set; }
    [JsonPropertyName("promptId")] public string? PromptId { get; set; }
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("attributionSkill")] public string? AttributionSkill { get; set; }
    [JsonPropertyName("attributionAgent")] public string? AttributionAgent { get; set; }
    [JsonPropertyName("message")] public TranscriptMessage? Message { get; set; }

    /// <summary>user/system records: string or array of content blocks; ai-title: sometimes the title.</summary>
    [JsonPropertyName("content")] public JsonElement Content { get; set; }

    /// <summary>user records: structured result of the preceding tool call (shape varies by tool).</summary>
    [JsonPropertyName("toolUseResult")] public JsonElement ToolUseResult { get; set; }

    // ai-title records: observed shape is {"type":"ai-title","aiTitle":"…","sessionId":"…"};
    // the other candidates are kept as drift tolerance.
    [JsonPropertyName("aiTitle")] public string? AiTitle { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class TranscriptMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("usage")] public UsageInfo? Usage { get; set; }
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
}

public sealed class UsageInfo
{
    [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public long CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public long CacheReadInputTokens { get; set; }
    [JsonPropertyName("cache_creation")] public CacheCreationInfo? CacheCreation { get; set; }
    [JsonPropertyName("server_tool_use")] public ServerToolUseInfo? ServerToolUse { get; set; }
    [JsonPropertyName("service_tier")] public string? ServiceTier { get; set; }
    // usage.iterations[] deliberately has no property: it duplicates the top-level counters
    // and summing it double-counts. Top-level fields are the only truth.
}

public sealed class CacheCreationInfo
{
    [JsonPropertyName("ephemeral_5m_input_tokens")] public long Ephemeral5m { get; set; }
    [JsonPropertyName("ephemeral_1h_input_tokens")] public long Ephemeral1h { get; set; }
}

public sealed class ServerToolUseInfo
{
    [JsonPropertyName("web_search_requests")] public long WebSearchRequests { get; set; }
    [JsonPropertyName("web_fetch_requests")] public long WebFetchRequests { get; set; }
}

/// <summary>Sidecar agent-&lt;id&gt;.meta.json next to a subagent transcript.</summary>
public sealed class AgentMetaFile
{
    [JsonPropertyName("agentType")] public string? AgentType { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("spawnDepth")] public int? SpawnDepth { get; set; }
    [JsonPropertyName("toolUseId")] public string? ToolUseId { get; set; }
    [JsonPropertyName("parentAgentId")] public string? ParentAgentId { get; set; }
}

/// <summary>Sidecar agent-&lt;id&gt;.forked-skill.json (present when the agent runs a forked skill).</summary>
public sealed class ForkedSkillFile
{
    [JsonPropertyName("skillName")] public string? SkillName { get; set; }
    [JsonPropertyName("attributionName")] public string? AttributionName { get; set; }
    [JsonPropertyName("effort")] public string? Effort { get; set; }
}

/// <summary>Payload of a &lt;forked-skill-launch&gt;{…}&lt;/forked-skill-launch&gt; system record.</summary>
public sealed class ForkedSkillLaunch
{
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("skillName")] public string? SkillName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TranscriptRecord))]
[JsonSerializable(typeof(AgentMetaFile))]
[JsonSerializable(typeof(ForkedSkillFile))]
[JsonSerializable(typeof(ForkedSkillLaunch))]
public sealed partial class TranscriptJsonContext : JsonSerializerContext;
