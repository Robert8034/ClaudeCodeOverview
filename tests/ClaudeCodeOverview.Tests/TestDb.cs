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

    public static List<byte[]> Lines(string name) =>
        File.ReadAllLines(PathOf(name))
            .Where(l => l.Length > 0)
            .Select(System.Text.Encoding.UTF8.GetBytes)
            .ToList();
}
