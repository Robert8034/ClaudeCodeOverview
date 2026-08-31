using ClaudeCodeOverview.Core.Notifications;
using ClaudeCodeOverview.Core.Queries;
using ClaudeCodeOverview.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ClaudeCodeOverview.Web.Components.Pages;

/// <summary>
/// Base for dashboard pages: reloads on global-filter changes and (coalesced) ingestion
/// deltas with a latest-wins gate. DataVersion is used as @key on charts so they re-create
/// with fresh data instead of diffing stale options.
/// </summary>
public abstract class DashboardPage : ComponentBase, IDisposable
{
    [Inject] protected GlobalFilterState Filters { get; set; } = default!;
    [Inject] protected IDashboardQueries Queries { get; set; } = default!;
    [Inject] protected IIngestionNotifier Notifier { get; set; } = default!;

    [CascadingParameter(Name = "IsDark")] protected bool IsDark { get; set; }

    protected bool Loaded { get; private set; }
    protected int DataVersion { get; private set; }

    private bool _refreshing;

    protected override async Task OnInitializedAsync()
    {
        Filters.Changed += OnFiltersChanged;
        Notifier.Changed += OnIngestion;
        await ReloadAsync();
    }

    /// <summary>Set false on heavy drill-down pages that should refresh on navigation only.</summary>
    protected virtual bool LiveUpdates => true;

    protected abstract Task LoadAsync();

    protected async Task ReloadAsync()
    {
        if (_refreshing) return; // latest-wins: a running refresh already reads current data
        _refreshing = true;
        try
        {
            await LoadAsync();
            DataVersion++;
            Loaded = true;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnFiltersChanged() =>
        _ = InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); });

    private void OnIngestion(IngestionDelta delta)
    {
        if (!LiveUpdates) return;
        _ = InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); });
    }

    public void Dispose()
    {
        Filters.Changed -= OnFiltersChanged;
        Notifier.Changed -= OnIngestion;
    }
}
