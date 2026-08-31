using ClaudeCodeOverview.Core.Ingestion;

namespace ClaudeCodeOverview.Core.Pricing;

/// <summary>One pricing row; all rates are USD per million tokens.</summary>
public sealed record PricingRow(
    string ModelPattern, double InUsd, double OutUsd,
    double CacheW5mUsd, double CacheW1hUsd, double CacheRUsd);

/// <summary>
/// The single home of the cost and cache-savings formulas.
/// A Pro subscription is flat-fee: every figure computed here is "estimated value at API
/// prices", never a bill — the UI must present it that way.
/// Unknown models return null (surfaced in the UI), never a silent 0.
/// </summary>
public sealed class CostCalculator
{
    private readonly List<PricingRow> _rows;

    public CostCalculator(IEnumerable<PricingRow> rows)
    {
        // Longest pattern first so prefix matching is deterministic.
        _rows = rows.OrderByDescending(r => r.ModelPattern.Length).ToList();
    }

    public PricingRow? Match(string model) =>
        _rows.FirstOrDefault(r => model.StartsWith(r.ModelPattern, StringComparison.Ordinal));

    public (double? CostUsd, double? SavingsUsd) Compute(UsageRow u) =>
        Compute(u.Model, u.InputTokens, u.OutputTokens, u.CacheCreation, u.CacheRead, u.Cache5m, u.Cache1h);

    public (double? CostUsd, double? SavingsUsd) Compute(
        string model, long input, long output, long cacheCreation, long cacheRead,
        long cache5m, long cache1h)
    {
        var p = Match(model);
        if (p is null) return (null, null);

        // The 5m/1h breakdown can be absent on older records: bill all cache writes at 5m then.
        var c5m = cache5m;
        var c1h = cache1h;
        if (c5m == 0 && c1h == 0 && cacheCreation > 0) c5m = cacheCreation;

        const double M = 1_000_000d;
        var cost = (input * p.InUsd + output * p.OutUsd
                    + c5m * p.CacheW5mUsd + c1h * p.CacheW1hUsd
                    + cacheRead * p.CacheRUsd) / M;

        // NET savings: what cache reads saved vs. fresh input, minus the write premiums paid.
        // A gross formula overstates savings and can mask a net loss under heavy 1h writes.
        var savings = (cacheRead * (p.InUsd - p.CacheRUsd)
                       - c5m * (p.CacheW5mUsd - p.InUsd)
                       - c1h * (p.CacheW1hUsd - p.InUsd)) / M;

        return (cost, savings);
    }

    /// <summary>Default seed: Anthropic API list prices, verified 2026-08. Editable in the UI.</summary>
    public static IReadOnlyList<PricingRow> DefaultSeed { get; } =
    [
        new("claude-fable-5",    10.00, 50.00, 12.50, 20.00, 1.00),
        new("claude-opus-5",      5.00, 25.00,  6.25, 10.00, 0.50),
        new("claude-opus-4",      5.00, 25.00,  6.25, 10.00, 0.50),
        new("claude-sonnet-5",    2.00, 10.00,  2.50,  4.00, 0.20),
        new("claude-sonnet-4-6",  3.00, 15.00,  3.75,  6.00, 0.30),
        new("claude-haiku-4-5",   1.00,  5.00,  1.25,  2.00, 0.10),
    ];
}
