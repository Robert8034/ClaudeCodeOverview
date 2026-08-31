using ClaudeCodeOverview.Core;
using ClaudeCodeOverview.Core.Derived;
using ClaudeCodeOverview.Core.Queries;
using Microsoft.Extensions.Options;

namespace ClaudeCodeOverview.Web.Services;

/// <summary>
/// Per-circuit global dashboard filters. Pages read <see cref="Filter"/> and subscribe to
/// <see cref="Changed"/>; the filter bar mutates it.
/// </summary>
public sealed class GlobalFilterState(IOptions<ClaudeOverviewOptions> options)
{
    public event Action? Changed;

    public string Preset { get; private set; } = "30d";
    public IReadOnlyCollection<long> SelectedProjects { get; set; } = [];
    public IReadOnlyCollection<string> SelectedModels { get; set; } = [];
    public bool ShowEur { get; private set; } =
        string.Equals(options.Value.Currency.Display, "EUR", StringComparison.OrdinalIgnoreCase);

    public double UsdToEur => options.Value.Currency.UsdToEur;

    public string TodayLocal => TimeBuckets.DayLocal(DateTimeOffset.UtcNow);

    public QueryFilter Filter
    {
        get
        {
            var today = DateTimeOffset.UtcNow;
            string? from = Preset switch
            {
                "today" => TimeBuckets.DayLocal(today),
                "7d" => TimeBuckets.DayLocal(today.AddDays(-6)),
                "30d" => TimeBuckets.DayLocal(today.AddDays(-29)),
                "90d" => TimeBuckets.DayLocal(today.AddDays(-89)),
                _ => null,
            };
            return new QueryFilter(
                from, null,
                SelectedProjects.Any() ? SelectedProjects.ToArray() : null,
                SelectedModels.Any() ? SelectedModels.ToArray() : null);
        }
    }

    public void SetPreset(string preset) { Preset = preset; Notify(); }
    public void SetProjects(IEnumerable<long> projects) { SelectedProjects = projects.ToList(); Notify(); }
    public void SetModels(IEnumerable<string> models) { SelectedModels = models.ToList(); Notify(); }
    public void SetEur(bool eur) { ShowEur = eur; Notify(); }

    /// <summary>Every cost figure is an estimate at API prices — a Pro subscription is flat-fee.</summary>
    public string Cost(double usd) => ShowEur
        ? (usd * UsdToEur).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("nl-NL")) + " €"
        : "$" + usd.ToString("N2");

    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000d).ToString("N1") + " B",
        >= 1_000_000 => (n / 1_000_000d).ToString("N1") + " M",
        >= 1_000 => (n / 1_000d).ToString("N1") + " k",
        _ => n.ToString(),
    };

    private void Notify() => Changed?.Invoke();
}
