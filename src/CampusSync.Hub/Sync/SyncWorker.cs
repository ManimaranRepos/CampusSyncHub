using CampusSync.Hub.Data;
using CampusSync.Hub.Ethos;
using Microsoft.EntityFrameworkCore;

namespace CampusSync.Hub.Sync;

/// <summary>Raises an in-process event whenever sync state changes, so Blazor pages refresh live.</summary>
public class SyncNotifier
{
    public event Action? Changed;
    public DateTime? LastPollUtc { get; private set; }
    public string WorkerStatus { get; private set; } = "Starting";

    public void Notify() => Changed?.Invoke();
    public void Polled(string status) { LastPollUtc = DateTime.UtcNow; WorkerStatus = status; Notify(); }
}

/// <summary>
/// Polls Ethos /consume, hands each change notification to the processor, and persists
/// a checkpoint so a restart resumes exactly where it stopped.
/// </summary>
public class SyncWorker(
    IServiceProvider services,
    IDbContextFactory<SyncDbContext> dbFactory,
    SyncNotifier notifier,
    IConfiguration config,
    ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Sync:PollSeconds", 5));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var handled = await PollOnceAsync(stoppingToken);
                notifier.Polled(handled > 0 ? $"Processed {handled} notification(s)" : "Idle, waiting for changes");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Ethos poll failed; will retry next cycle");
                notifier.Polled($"Ethos unreachable: {ex.Message}");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var ethos = scope.ServiceProvider.GetRequiredService<IEthosClient>();
        var processor = scope.ServiceProvider.GetRequiredService<RegistrationSyncProcessor>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.SyncStates.FindAsync([SyncDbContext.CheckpointKey], ct);
        var lastId = long.TryParse(state?.Value, out var v) ? v : 0;

        var batch = await ethos.ConsumeAsync(lastId, config.GetValue("Sync:BatchSize", 20), ct);
        foreach (var notification in batch.OrderBy(n => n.Id))
        {
            await processor.ProcessAsync(notification, isReplay: false, ct);
            lastId = notification.Id;

            // Advance the checkpoint after every message: at-least-once delivery + idempotency = exactly-once effect.
            state ??= db.SyncStates.Add(new SyncState { Key = SyncDbContext.CheckpointKey }).Entity;
            state.Value = lastId.ToString();
            await db.SaveChangesAsync(ct);
        }
        return batch.Count;
    }
}
