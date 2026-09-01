using System.Text;
using System.Text.Json;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// Routes raw JSONL lines into a ParsedBatch. Pure and side-effect free: unknown record
/// types are counted (schema-drift telemetry), malformed lines become ParseErrors — the
/// parser itself never throws for bad input.
/// </summary>
public static class TranscriptLineParser
{
    public static ParsedBatch Parse(FileContext ctx, IReadOnlyList<byte[]> lines)
    {
        var batch = new ParsedBatch();
        string? lastCwd = null;
        DateTimeOffset? lastTs = null;

        for (var i = 0; i < lines.Count; i++)
        {
            TranscriptRecord? rec;
            try
            {
                rec = JsonSerializer.Deserialize(lines[i], TranscriptJsonContext.Default.TranscriptRecord);
            }
            catch (JsonException)
            {
                batch.Errors.Add(new ParseError(i, Snippet(lines[i])));
                continue;
            }
            if (rec is null) { batch.Errors.Add(new ParseError(i, Snippet(lines[i]))); continue; }

            var type = rec.Type ?? "(untyped)";
            batch.CountType(type);

            var sessionId = rec.SessionId ?? ctx.SessionIdFromPath;
            var ts = ParseTimestamp(rec.Timestamp) ?? lastTs;
            if (rec.Timestamp is not null && ts is not null) lastTs = ts;
            if (rec.Cwd is not null) lastCwd = rec.Cwd;
            var effectiveTs = ts ?? ctx.FileLastWriteUtc;

            switch (type)
            {
                case "assistant":
                    HandleAssistant(ctx, rec, sessionId, lastCwd, effectiveTs, batch);
                    break;
                case "user":
                    ToolEventExtractor.ExtractToolResults(rec, batch.ToolResults);
                    if (SkillExtractor.TryExtractInSession(rec, out var skillName, out var skillArgs))
                    {
                        batch.Skills.Add(new SkillRow(
                            rec.Uuid ?? $"{ctx.FileId}:{i}", sessionId, lastCwd, effectiveTs,
                            skillName, "in_session", rec.AgentId ?? ctx.AgentId, skillArgs));
                    }
                    // Newer CLI builds drop isMeta/turnCompanion/skill-format and emit a record whose
                    // whole content is command markers — for skills and built-in commands alike.
                    else if (SkillExtractor.TryExtractMarkerOnlyCommand(rec, out var markerName, out var markerArgs))
                    {
                        batch.Skills.Add(new SkillRow(
                            rec.Uuid ?? $"{ctx.FileId}:{i}", sessionId, lastCwd, effectiveTs,
                            markerName, SkillExtractor.ClassifyShape(markerName),
                            rec.AgentId ?? ctx.AgentId, markerArgs));
                    }
                    batch.TouchSession(sessionId, lastCwd, ts, rec.GitBranch, rec.Version);
                    break;
                case "system":
                    // Order matters: a forked-skill-launch record ALSO carries subtype
                    // "local_command" (observed) — classify it as forked first.
                    if (SkillExtractor.TryExtractForkedLaunch(rec, out var launch))
                    {
                        var forkedName = SkillExtractor.NormalizeName(launch.SkillName!);
                        batch.Skills.Add(new SkillRow(
                            rec.Uuid ?? $"{ctx.FileId}:{i}", sessionId, lastCwd, effectiveTs,
                            forkedName, "forked", launch.AgentId, launch.Description));
                        if (launch.AgentId is not null)
                            batch.ForkedAgents[launch.AgentId] = forkedName;
                    }
                    else if (SkillExtractor.TryExtractLocalCommand(rec, out var cmdName, out var cmdArgs))
                    {
                        batch.Skills.Add(new SkillRow(
                            rec.Uuid ?? $"{ctx.FileId}:{i}", sessionId, lastCwd, effectiveTs,
                            cmdName, SkillExtractor.ClassifyShape(cmdName), null, cmdArgs));
                    }
                    batch.TouchSession(sessionId, lastCwd, ts, rec.GitBranch, rec.Version);
                    break;
                case "ai-title":
                    var title = ExtractTitle(rec);
                    if (title is not null)
                        batch.TouchSession(sessionId, lastCwd, ts, rec.GitBranch, rec.Version, title);
                    break;
                default:
                    // Counted above; availability records (attachment/skill_listing etc.) are not usage.
                    break;
            }
        }

        return batch;
    }

    private static void HandleAssistant(
        FileContext ctx, TranscriptRecord rec, string sessionId, string? cwd,
        DateTimeOffset ts, ParsedBatch batch)
    {
        var agentId = rec.AgentId ?? ctx.AgentId;
        batch.TouchSession(sessionId, cwd, ts, rec.GitBranch, rec.Version);

        var m = rec.Message;
        if (m?.Id is not null && m.Usage is { } u)
        {
            batch.UsageRows.Add(new UsageRow(
                m.Id, sessionId, agentId, cwd, ts, m.Model ?? "(unknown)",
                u.InputTokens, u.OutputTokens, u.CacheCreationInputTokens, u.CacheReadInputTokens,
                u.CacheCreation?.Ephemeral5m ?? 0, u.CacheCreation?.Ephemeral1h ?? 0,
                u.ServerToolUse?.WebSearchRequests ?? 0, u.ServerToolUse?.WebFetchRequests ?? 0,
                u.ServiceTier,
                rec.AttributionSkill ?? ctx.ForkedSkillName,
                rec.RequestId, rec.Effort));
        }

        if (m is not null)
            ToolEventExtractor.ExtractToolUses(m.Content, sessionId, agentId, cwd, ts, batch.ToolUses);
    }

    private static string? ExtractTitle(TranscriptRecord rec)
    {
        if (!string.IsNullOrWhiteSpace(rec.AiTitle)) return rec.AiTitle;
        if (!string.IsNullOrWhiteSpace(rec.Title)) return rec.Title;
        if (rec.Value.ValueKind == JsonValueKind.String) return rec.Value.GetString();
        if (rec.Content.ValueKind == JsonValueKind.String) return rec.Content.GetString();
        return null;
    }

    internal static DateTimeOffset? ParseTimestamp(string? value) =>
        value is not null && DateTimeOffset.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var ts)
            ? ts
            : null;

    private static string Snippet(byte[] line)
    {
        var len = Math.Min(line.Length, 200);
        return Encoding.UTF8.GetString(line, 0, len);
    }
}
