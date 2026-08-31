using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClaudeCodeOverview.Core.Data;

/// <summary>Applies embedded NNN_*.sql migration scripts in name order, tracked in schema_version.</summary>
public static class Migrator
{
    public static void Migrate(SqliteConnection conn)
    {
        conn.Execute("CREATE TABLE IF NOT EXISTS schema_version (version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);");

        var assembly = typeof(Migrator).Assembly;
        var prefix = typeof(Migrator).Namespace + ".Migrations.";
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var applied = conn.Query<string>("SELECT version FROM schema_version").ToHashSet();

        foreach (var resource in scripts)
        {
            var version = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            if (applied.Contains(version)) continue;

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded migration {resource}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            using var tx = conn.BeginTransaction();
            conn.Execute(sql, transaction: tx);
            conn.Execute("INSERT INTO schema_version(version, applied_utc) VALUES(@v, @ts)",
                new { v = version, ts = DateTimeOffset.UtcNow.ToString("O") }, tx);
            tx.Commit();
        }
    }
}
