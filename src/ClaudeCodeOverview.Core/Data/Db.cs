using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Core.Data;

/// <summary>
/// Connection factory. One long-lived WRITE connection is owned by the ingestion service
/// (single writer); UI/queries open short-lived read connections — WAL gives concurrent readers.
/// </summary>
public static class Db
{
    public static SqliteConnection Open(string dbPath, bool readOnly = false)
    {
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL;");
        conn.Execute("PRAGMA synchronous=NORMAL;");
        conn.Execute("PRAGMA busy_timeout=5000;");
        conn.Execute("PRAGMA foreign_keys=ON;");
        return conn;
    }
}
