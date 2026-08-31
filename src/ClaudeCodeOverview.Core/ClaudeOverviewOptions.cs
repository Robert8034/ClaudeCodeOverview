using ClaudeCodeOverview.Core.Pricing;

namespace ClaudeCodeOverview.Core;

public sealed class ClaudeOverviewOptions
{
    public const string SectionName = "ClaudeOverview";

    /// <summary>Transcript root. Production: the Syncthing mirror; dev: your own ~/.claude/projects.</summary>
    public string? DataRoot { get; set; }

    public string? DatabasePath { get; set; }
    public int Port { get; set; } = 5199;
    public int RescanIntervalMinutes { get; set; } = 5;
    public int DebounceMs { get; set; } = 300;
    public CurrencyOptions Currency { get; set; } = new();

    /// <summary>Seeds the pricing table on first run only; afterwards the table is the source of truth.</summary>
    public List<PricingSeedOption> Pricing { get; set; } = [];

    public string ResolveDataRoot() =>
        DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public string ResolveDatabasePath() =>
        DatabasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeOverview", "usage.db");

    public IEnumerable<PricingRow> PricingSeed() =>
        Pricing.Count > 0
            ? Pricing.Select(p => new PricingRow(p.ModelPattern, p.InUsd, p.OutUsd, p.CacheW5mUsd, p.CacheW1hUsd, p.CacheRUsd))
            : CostCalculator.DefaultSeed;
}

public sealed class CurrencyOptions
{
    public string Display { get; set; } = "USD";
    public double UsdToEur { get; set; } = 0.86;
}

public sealed class PricingSeedOption
{
    public string ModelPattern { get; set; } = "";
    public double InUsd { get; set; }
    public double OutUsd { get; set; }
    public double CacheW5mUsd { get; set; }
    public double CacheW1hUsd { get; set; }
    public double CacheRUsd { get; set; }
}
