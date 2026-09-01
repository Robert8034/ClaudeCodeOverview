using ClaudeCodeOverview.Core.Data;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Tests;

/// <summary>Fresh migrated SQLite database in a temp file, deleted on dispose.</summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ccov-test-{Guid.NewGuid():N}.db");

    public SqliteConnection Connection { get; }

    public TestDb()
    {
        Connection = Db.Open(Path);
        Migrator.Migrate(Connection);
    }

    public void Dispose()
    {
        Connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(Path);
            File.Delete(Path + "-wal");
            File.Delete(Path + "-shm");
        }
        catch (IOException) { /* temp files; the OS cleans up eventually */ }
    }
}

public static class Fixtures
{
    public static string PathOf(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Runs one fixture file through the real tail → parse → apply pipeline.</summary>
    public static Core.Ingestion.ParsedBatch Ingest(
        TestDb db, Core.Data.IngestRepository repo, string fixtureName)
    {
        var costs = new Core.Pricing.CostCalculator(Core.Pricing.CostCalculator.DefaultSeed);
        var path = PathOf(fixtureName);
        var info = Core.Ingestion.TranscriptPaths.Classify(path);
        var (fileId, offset) = repo.EnsureFile(db.Connection, path, info.SessionId, info.AgentId);
        var tail = Core.Ingestion.FileTailer.ReadNewLines(path, offset);
        var ctx = new Core.Ingestion.FileContext(
            fileId, path, info.SessionId, info.AgentId, info.WorkflowId, null, DateTimeOffset.UtcNow);
        var batch = Core.Ingestion.TranscriptLineParser.Parse(ctx, tail.Lines);
        repo.ApplyBatch(db.Connection, ctx, batch, tail.NewOffset, new FileInfo(path).Length,
            DateTimeOffset.UtcNow, costs);
        Core.Derived.BlockCalculator.RebuildAll(db.Connection);
        return batch;
    }

    public static List<byte[]> Lines(string name) =>
        File.ReadAllLines(PathOf(name))
            .Where(l => l.Length > 0)
            .Select(System.Text.Encoding.UTF8.GetBytes)
            .ToList();
}
