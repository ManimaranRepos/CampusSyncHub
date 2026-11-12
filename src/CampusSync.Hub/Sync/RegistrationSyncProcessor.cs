using System.Diagnostics;
using System.Text.Json;
using CampusSync.Hub.Data;
using CampusSync.Hub.Ethos;
using CampusSync.Hub.Targets;
using Microsoft.EntityFrameworkCore;

namespace CampusSync.Hub.Sync;

public enum ProcessOutcome { Synced, Failed, Duplicate, Ignored }

/// <summary>
/// Core business logic: turns one Ethos "section-registrations created" notification into
/// LMS enrollment + library account + welcome email. Idempotent and dead-letter aware.
/// </summary>
public class RegistrationSyncProcessor(
    IDbContextFactory<SyncDbContext> dbFactory,
    IEthosClient ethos,
    ITargetGateway targets,
    SyncNotifier notifier,
    ILogger<RegistrationSyncProcessor> log)
{
    public async Task<ProcessOutcome> ProcessAsync(EthosNotification n, bool isReplay, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (n.Resource.Name != "section-registrations" || n.Operation is not ("created" or "replaced"))
        {
            await MarkProcessedAsync(db, n.Id, ct);
            return ProcessOutcome.Ignored;
        }

        // Idempotency: a notification is handled once unless explicitly replayed from the dead-letter queue.
        if (!isReplay && await db.ProcessedNotifications.AnyAsync(p => p.NotificationId == n.Id, ct))
            return ProcessOutcome.Duplicate;

        var sw = Stopwatch.StartNew();
        var steps = new List<string>();
        var entry = new SyncLog { NotificationId = n.Id };

        try
        {
            var registration = n.Content.Deserialize<SectionRegistration>(EthosClient.Json)
                ?? await ethos.GetAsync<SectionRegistration>("section-registrations", n.Resource.Id, ct);

            // Enrich from Ethos: the notification only carries ids.
            var person = await ethos.GetAsync<EthosPerson>("persons", registration.Registrant.Id, ct);
            var section = await ethos.GetAsync<EthosSection>("sections", registration.Section.Id, ct);
            entry.StudentName = person.FullName;
            entry.StudentEmail = person.SchoolEmail ?? "";
            entry.SectionCode = section.Code;

            await targets.EnrollInLmsAsync(person.StudentId, person.FullName, section.Code, section.Title, ct);
            steps.Add("LMS ✓");

            await targets.EnsureLibraryAccountAsync(person.StudentId, person.FullName, person.SchoolEmail ?? "", ct);
            steps.Add("Library ✓");

            if (!string.IsNullOrEmpty(person.SchoolEmail))
            {
                await targets.SendEmailAsync(person.SchoolEmail,
                    $"You're registered for {section.Code}",
                    $"Hi {person.Names.First().FirstName}, you are enrolled in {section.Title} ({section.Code}). " +
                    "Your LMS course and library access are ready.", ct);
                steps.Add("Email ✓");
            }

            entry.Status = isReplay ? SyncStatus.Retried : SyncStatus.Success;
            var dead = await db.DeadLetters.FindAsync([n.Id], ct);
            if (dead is not null) db.DeadLetters.Remove(dead);
            log.LogInformation("Synced registration {Notification} for {Student} → {Section}", n.Id, person.FullName, section.Code);
            return await FinishAsync(ProcessOutcome.Synced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add("✗");
            entry.Status = SyncStatus.Failed;
            entry.Error = ex.Message;

            var dead = await db.DeadLetters.FindAsync([n.Id], ct);
            if (dead is null)
                db.DeadLetters.Add(dead = new DeadLetter { NotificationId = n.Id, Payload = JsonSerializer.Serialize(n, EthosClient.Json) });
            dead.Attempts++;
            dead.Error = ex.Message;
            dead.LastAttempt = DateTime.UtcNow;

            log.LogWarning(ex, "Registration {Notification} failed after retries; moved to dead-letter queue", n.Id);
            return await FinishAsync(ProcessOutcome.Failed);
        }

        async Task<ProcessOutcome> FinishAsync(ProcessOutcome outcome)
        {
            entry.Steps = string.Join(" · ", steps);
            entry.DurationMs = sw.Elapsed.TotalMilliseconds;
            db.SyncLogs.Add(entry);
            await MarkProcessedAsync(db, n.Id, ct); // also saves changes
            notifier.Notify();
            return outcome;
        }
    }

    /// <summary>Replays a dead-lettered notification (Retry button on the dashboard).</summary>
    public async Task<ProcessOutcome> ReplayAsync(long notificationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dead = await db.DeadLetters.FindAsync([notificationId], ct)
            ?? throw new InvalidOperationException($"Notification {notificationId} is not in the dead-letter queue.");
        var notification = JsonSerializer.Deserialize<EthosNotification>(dead.Payload, EthosClient.Json)!;
        return await ProcessAsync(notification, isReplay: true, ct);
    }

    private static async Task MarkProcessedAsync(SyncDbContext db, long id, CancellationToken ct)
    {
        if (await db.ProcessedNotifications.FindAsync([id], ct) is null)
            db.ProcessedNotifications.Add(new ProcessedNotification { NotificationId = id });
        await db.SaveChangesAsync(ct);
    }
}
