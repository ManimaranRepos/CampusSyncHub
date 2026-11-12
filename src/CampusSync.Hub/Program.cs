using CampusSync.Hub.Components;
using CampusSync.Hub.Data;
using CampusSync.Hub.Ethos;
using CampusSync.Hub.Sync;
using CampusSync.Hub.Targets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddDbContextFactory<SyncDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Sync") ?? "Data Source=campussync.db"));

// Shared resilience policy: retry with exponential back-off + jitter, circuit breaker, timeouts.
void Resilience(HttpStandardResilienceOptions o)
{
    o.Retry.MaxRetryAttempts = 3;
    o.Retry.Delay = TimeSpan.FromMilliseconds(400);
    o.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
    o.Retry.UseJitter = true;
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
}

builder.Services.AddHttpClient<IEthosClient, EthosClient>(c =>
        c.BaseAddress = new Uri(builder.Configuration["Ethos:BaseUrl"]!))
    .AddStandardResilienceHandler(Resilience);

builder.Services.AddHttpClient<ITargetGateway, TargetGateway>(c =>
        c.BaseAddress = new Uri(builder.Configuration["Targets:BaseUrl"]!))
    .AddStandardResilienceHandler(Resilience);

// Plain clients for the dashboard's demo buttons and downstream counters.
builder.Services.AddHttpClient("ethos-demo", c => c.BaseAddress = new Uri(builder.Configuration["Ethos:BaseUrl"]!));
builder.Services.AddHttpClient("targets", c => c.BaseAddress = new Uri(builder.Configuration["Targets:BaseUrl"]!));

builder.Services.AddSingleton<SyncNotifier>();
builder.Services.AddTransient<RegistrationSyncProcessor>();
builder.Services.AddSingleton<SyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncWorker>());

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<SyncDbContext>>().CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();

// Small JSON API: handy for monitoring and for the demo script.
app.MapGet("/api/stats", async (IDbContextFactory<SyncDbContext> f, SyncNotifier n) =>
{
    await using var db = await f.CreateDbContextAsync();
    return new
    {
        processed = await db.ProcessedNotifications.CountAsync(),
        success = await db.SyncLogs.CountAsync(l => l.Status == SyncStatus.Success || l.Status == SyncStatus.Retried),
        failedAttempts = await db.SyncLogs.CountAsync(l => l.Status == SyncStatus.Failed),
        deadLetters = await db.DeadLetters.CountAsync(),
        checkpoint = (await db.SyncStates.FindAsync(SyncDbContext.CheckpointKey))?.Value,
        worker = n.WorkerStatus,
        lastPollUtc = n.LastPollUtc
    };
});
app.MapGet("/api/logs", async (IDbContextFactory<SyncDbContext> f) =>
{
    await using var db = await f.CreateDbContextAsync();
    return await db.SyncLogs.OrderByDescending(l => l.Id).Take(20).ToListAsync();
});
app.MapPost("/api/dead-letters/{id:long}/retry", async (long id, RegistrationSyncProcessor p, CancellationToken ct) =>
    Results.Ok(new { outcome = (await p.ReplayAsync(id, ct)).ToString() }));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
