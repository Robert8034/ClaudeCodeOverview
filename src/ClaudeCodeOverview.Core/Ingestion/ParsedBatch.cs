namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>Static context for one transcript file being ingested.</summary>
public sealed record FileContext(
    long FileId,
    string FilePath,
    string SessionIdFromPath,
    string? AgentId,
    string? WorkflowId,
    string? ForkedSkillName,
    DateTimeOffset FileLastWriteUtc);

public sealed record UsageRow(
    string MessageId, string SessionId, string? AgentId, string? Cwd,
    DateTimeOffset TsUtc, string Model,
    long InputTokens, long OutputTokens, long CacheCreation, long CacheRead,
    long Cache5m, long Cache1h, long WebSearch, long WebFetch,
    string? ServiceTier, string? AttributionSkill, string? RequestId, string? Effort);

public sealed record ToolUseRow(
    string ToolUseId, string SessionId, string? AgentId, string? Cwd,
    DateTimeOffset TsUtc, string ToolName, bool IsMcp, bool IsGitCommit);

public sealed record ToolResultRow(string ToolUseId, bool IsError, int? LinesAdded, int? LinesRemoved);

public sealed record SkillRow(
    string RecordUuid, string SessionId, string? Cwd, DateTimeOffset TsUtc,
    string SkillName, string Shape, string? AgentId, string? Args);

public sealed record SessionTouch(
    string SessionId, string? Cwd, DateTimeOffset? TsUtc, string? GitBranch, string? CliVersion, string? Title);

public sealed record ParseError(int LineIndex, string Snippet);

/// <summary>Everything one parse pass extracted; applied to SQLite in a single transaction.</summary>
public sealed class ParsedBatch
{
    public List<UsageRow> UsageRows { get; } = [];
    public List<ToolUseRow> ToolUses { get; } = [];
    public List<ToolResultRow> ToolResults { get; } = [];
    public List<SkillRow> Skills { get; } = [];
    public Dictionary<string, SessionTouch> Sessions { get; } = [];
    /// <summary>agentId → skillName discovered via forked-skill-launch records.</summary>
    public Dictionary<string, string> ForkedAgents { get; } = [];
    public Dictionary<string, int> RecordTypeCounts { get; } = [];
    public List<ParseError> Errors { get; } = [];

    /// <summary>Record types the parser understands; anything else counts as schema drift.</summary>
    public static readonly HashSet<string> KnownTypes =
    [
        "assistant", "user", "system", "attachment", "mode", "permission-mode", "ai-title",
        "last-prompt", "file-history-snapshot", "file-history-delta", "queue-operation", "atis-latch",
    ];

    public int UnknownTypeCount =>
        RecordTypeCounts.Where(kv => !KnownTypes.Contains(kv.Key)).Sum(kv => kv.Value);

    public void CountType(string type) =>
        RecordTypeCounts[type] = RecordTypeCounts.GetValueOrDefault(type) + 1;

    public void TouchSession(string sessionId, string? cwd, DateTimeOffset? ts, string? branch, string? version, string? title = null)
    {
        if (Sessions.TryGetValue(sessionId, out var prev))
        {
            Sessions[sessionId] = new SessionTouch(
                sessionId,
                prev.Cwd ?? cwd,                       // first-seen cwd wins for session identity
                Latest(prev.TsUtc, ts),
                branch ?? prev.GitBranch,
                version ?? prev.CliVersion,
                title ?? prev.Title);
        }
        else
        {
            Sessions[sessionId] = new SessionTouch(sessionId, cwd, ts, branch, version, title);
        }
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    public bool IsEmpty =>
        UsageRows.Count == 0 && ToolUses.Count == 0 && ToolResults.Count == 0 &&
        Skills.Count == 0 && Sessions.Count == 0 && RecordTypeCounts.Count == 0 && Errors.Count == 0;
}
