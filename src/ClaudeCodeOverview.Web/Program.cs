using ClaudeCodeOverview.Core;
using ClaudeCodeOverview.Core.Ingestion;
using ClaudeCodeOverview.Core.Notifications;
using ClaudeCodeOverview.Core.Queries;
using ClaudeCodeOverview.Web.Components;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClaudeOverviewOptions>(
    builder.Configuration.GetSection(ClaudeOverviewOptions.SectionName));

builder.Services.AddSingleton<IIngestionNotifier>(new IngestionNotifier());
builder.Services.AddHostedService<IngestionService>();
builder.Services.AddSingleton<IDashboardQueries>(sp =>
    new DashboardQueries(sp.GetRequiredService<IOptions<ClaudeOverviewOptions>>().Value.ResolveDatabasePath()));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// LAN-only plain HTTP by design (v1): the app runs on the home server; no TLS/auth layer here.
var port = builder.Configuration.GetSection(ClaudeOverviewOptions.SectionName)
    .Get<ClaudeOverviewOptions>()?.Port ?? 5199;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
