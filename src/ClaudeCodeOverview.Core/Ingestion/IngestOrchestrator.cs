using ClaudeCodeOverview.Core.Data;
using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Notifications;
using ClaudeCodeOverview.Core.Pricing;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeOverview.Core.Ingestion;

/// <summary>
/// Drives one file through tail → parse → apply. Owns the write connection's usage for
/// ingestion; everything here runs on the single consumer thread of the ingest channel.
/// </summary>
public sealed class IngestOrchestrator(
    SqliteConnection conn,
    IngestRepository repo,
    IIngestionNotifier notifier,
    ILogger logger)
{
    private CostCalculator _costs = new(CostCalculator.DefaultSeed);

    public void ReloadPricing() => _costs = new CostCalculator(repo.LoadPricing(conn));

    public void IngestFile(string path)
    {
        if (!TranscriptPaths.IsIngestible(path)) return;

        var info = TranscriptPaths.Classify(path);
        var (fileId, offset) = repo.EnsureFile(conn, path, info.SessionId, info.AgentId);

        string? forkedSkillName = null;
        if (info.AgentId is not null)
        {
            var sidecars = AgentMetaReader.Read(path);
            repo.UpsertAgentSidecars(conn, info.AgentId, info.SessionId, info.WorkflowId, sidecars);
            forkedSkillName = sidecars.ForkedSkill?.SkillName is { } n ? SkillExtractor.NormalizeName(n) : null;
        }

        TailResult tail;
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists)
            {
                repo.MarkFileStatus(conn, fileId, "deleted");
                return;
            }
            tail = FileTailer.ReadNewLines(path, offset);
            if (tail.Truncated)
            {
                logger.LogWarning("File shrank below its offset, re-ingesting from scratch: {Path}", path);
                repo.ResetFile(conn, fileId, info.SessionId, info.AgentId);
                tail = FileTailer.ReadNewLines(path, 0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {Path}; the rescan will retry", path);
            return;
        }

        if (tail.Lines.Count == 0 && tail.OversizedSkipped == 0 && tail.NewOffset == offset) return;

        var ctx = new FileContext(
            fileId, path, info.SessionId, info.AgentId, info.WorkflowId, forkedSkillName,
            new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero));
        var batch = TranscriptLineParser.Parse(ctx, tail.Lines);
        for (var i = 0; i < tail.OversizedSkipped; i++)
            batch.Errors.Add(new ParseError(-1, "(line exceeded 64 MB and was skipped)"));

        var result = repo.ApplyBatch(conn, ctx, batch, tail.NewOffset, fi.Length, ctx.FileLastWriteUtc, _costs);

        if (batch.UsageRows.Count > 0) BlockCalculator.RebuildAll(conn);

        notifier.Publish(d =>
        {
            foreach (var p in result.ProjectIds) d.ProjectIds.Add(p);
            foreach (var s in result.SessionIds) d.SessionIds.Add(s);
            d.MergeDay(result.MinDayLocal);
            d.NewParseErrors |= result.NewParseErrors > 0;
            d.NewUnknownModels |= result.NewUnknownModels;
        });
    }

    /// <summary>New or changed files vs. ingested_files (size/mtime), plus deletion marking.</summary>
    public List<string> FindChangedFiles(string dataRoot)
    {
        var known = conn.Query<(string Path, long Offset, long? Size, string? Status)>(
                "SELECT path, byte_offset, file_size, status FROM ingested_files")
            .ToDictionary(r => r.Path, r => r, StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(dataRoot))
        {
            foreach (var path in Directory.EnumerateFiles(dataRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!TranscriptPaths.IsIngestible(path)) continue;
                seen.Add(path);
                if (!known.TryGetValue(path, out var k)) { changed.Add(path); continue; }
                long size;
                try { size = new FileInfo(path).Length; } catch (IOException) { continue; }
                if (size != k.Offset || size != (k.Size ?? -1) || k.Status != "active") changed.Add(path);
            }
        }

        foreach (var (path, row) in known)
        {
            if (row.Status == "active" && !seen.Contains(path))
            {
                conn.Execute("UPDATE ingested_files SET status='deleted' WHERE path=@path", new { path });
            }
        }
        return changed;
    }

    /// <summary>Sidecars can land after the agent JSONL (Syncthing ordering); retry until complete.</summary>
    public void RetryIncompleteAgentSidecars()
    {
        var pending = conn.Query<(string AgentId, string? Path, string? SessionId)>(
            """
            SELECT a.agent_id, f.path, f.session_id
            FROM agents a
            LEFT JOIN ingested_files f ON f.agent_id = a.agent_id
            WHERE a.meta_loaded = 0
            """).ToList();
        foreach (var (agentId, path, sessionId) in pending)
        {
            if (path is null || !File.Exists(path)) continue;
            var info = TranscriptPaths.Classify(path);
            var sidecars = AgentMetaReader.Read(path);
            if (sidecars.Meta is not null)
                repo.UpsertAgentSidecars(conn, agentId, sessionId ?? info.SessionId, info.WorkflowId, sidecars);
        }
    }
}
