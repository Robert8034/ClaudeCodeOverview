using ClaudeCodeOverview.Core.Pricing;
using Dapper;

namespace ClaudeCodeOverview.Core.Queries;

/// <summary>Global dashboard filter. Days are Europe/Amsterdam dates (matching day_local).</summary>
public sealed record QueryFilter(
    string? FromDayLocal = null,
    string? ToDayLocal = null,
    long[]? ProjectIds = null,
    string[]? Models = null);

public sealed record HeadlineStats(
    long InputTokens, long OutputTokens, long CacheCreation, long CacheRead,
    long Cache5m, long Cache1h, double CostUsd, double CacheSavingsUsd,
    long Sessions, long Turns, long ActiveSessions);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record DailyPoint(string DayLocal, string Key, long Tokens, double CostUsd);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record ModelMixRow(string Model, long Tokens, double CostUsd, long Turns, bool Unpriced);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record ProjectSummary(
    long ProjectId, string Cwd, string? Slug, long Tokens, double CostUsd,
    long Sessions, string? LastActivityUtc);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record SessionSummary(
    string SessionId, string? Title, string? GitBranch, string? FirstTsUtc, string? LastTsUtc,
    long Turns, long Tokens, double CostUsd, long SubagentCount, long ToolErrors);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record SessionTurn(string TsUtc, string Model, string? Effort, long InputTokens, long OutputTokens,
    long CacheCreation, long CacheRead, double? CostUsd, string? AgentId, string? AttributionSkill);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record SessionAgent(string AgentId, string? ParentAgentId, string? AgentType, string? Description,
    int? SpawnDepth, string? WorkflowId, string? SkillName, long Tokens, double CostUsd);

public sealed record SessionDetail(
    string SessionId, string? Title, string? GitBranch, string? CliVersion,
    List<SessionTurn> Turns, List<SessionAgent> Agents, List<string> Skills);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record SkillScorecardRow(
    string SkillName, long Invocations, long InvocationsLast30, long InvocationsPrior30,
    string Shapes, long AttributedTokens, double AttributedCostUsd,
    long AttributedToolCalls, long AttributedToolErrors, double? MedianRunSeconds);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record BuiltinCommandRow(string CommandName, long Invocations, string? LastUsedUtc);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record SkillDailyPoint(string DayLocal, long Invocations, long AttributedTokens);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record ToolUsageRow(string ToolName, long Calls, long Errors, double ErrorRate, bool IsMcp);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record AgentUsageRow(string AgentType, long Spawns, long Tokens, double CostUsd);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record HeatmapCell(string DayLocal, long Sessions, long Tokens, double CostUsd);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record BlockInfo(string StartUtc, string EndUtc, long Tokens, double CostUsd, long Messages);

public sealed record RateWindows(
    long Current5hTokens, double Current5hCostUsd,
    long Rolling7dTokens, double Rolling7dCostUsd,
    List<BlockInfo> RecentBlocks);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record ProductivityDay(string DayLocal, long LinesAdded, long LinesRemoved, long Commits);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record DurationBucket(string Label, long SessionCount);

[method: ExplicitConstructor] // bind by name: expression columns report as BLOB on an empty result set
public sealed record ParseErrorRow(string File, long? LineNo, string TsUtc, string? Snippet);

public sealed record DataHealth(
    long FilesActive, long FilesDeleted, long FilesError,
    long TotalParseErrors, long TotalUnknownTypes,
    Dictionary<string, long> UnknownRecordTypes,
    List<string> UnknownModels,
    List<ParseErrorRow> RecentParseErrors,
    string? LastIngestUtc,
    long UsageEventCount);

public interface IDashboardQueries
{
    Task<HeadlineStats> GetHeadlineStatsAsync(QueryFilter f);
    Task<List<DailyPoint>> GetDailyByModelAsync(QueryFilter f);
    Task<List<DailyPoint>> GetDailyByProjectAsync(QueryFilter f);
    Task<List<ModelMixRow>> GetModelMixAsync(QueryFilter f);
    Task<List<ProjectSummary>> GetProjectSummariesAsync(QueryFilter f);
    Task<List<SessionSummary>> GetSessionsAsync(QueryFilter f, long projectId);
    Task<SessionDetail?> GetSessionDetailAsync(string sessionId);
    Task<List<SkillScorecardRow>> GetSkillScorecardAsync(QueryFilter f, string todayLocal);
    Task<List<BuiltinCommandRow>> GetBuiltinCommandsAsync(QueryFilter f);
    Task<List<SkillDailyPoint>> GetSkillDailyAsync(QueryFilter f, string skillName);
    Task<List<ToolUsageRow>> GetToolUsageAsync(QueryFilter f);
    Task<List<AgentUsageRow>> GetAgentUsageAsync(QueryFilter f);
    Task<List<HeatmapCell>> GetActivityHeatmapAsync(string fromDayLocal, string toDayLocal);
    Task<RateWindows> GetRateWindowsAsync(DateTimeOffset nowUtc);
    Task<List<ProductivityDay>> GetProductivityDailyAsync(QueryFilter f);
    Task<List<DurationBucket>> GetSessionDurationHistogramAsync(QueryFilter f);
    Task<DataHealth> GetDataHealthAsync();
    Task<List<PricingRow>> GetPricingAsync();
    /// <summary>Upserts a pricing row, then re-derives cost/savings on all usage rows and rebuilds blocks.</summary>
    Task UpsertPricingAsync(PricingRow row);
    Task<List<string>> GetKnownModelsAsync();
    Task<List<(long Id, string Cwd)>> GetProjectsAsync();
}
