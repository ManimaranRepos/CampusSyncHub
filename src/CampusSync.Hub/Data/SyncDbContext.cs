using Microsoft.EntityFrameworkCore;

namespace CampusSync.Hub.Data;

public enum SyncStatus { Success, Failed, Retried, Skipped }

public class SyncLog
{
    public int Id { get; set; }
    public long NotificationId { get; set; }
    public string StudentName { get; set; } = "";
    public string StudentEmail { get; set; } = "";
    public string SectionCode { get; set; } = "";
    public SyncStatus Status { get; set; }
    public string Steps { get; set; } = "";       // e.g. "LMS ✓ · Library ✓ · Email ✓"
    public string? Error { get; set; }
    public double DurationMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Notifications that failed after all retries. Kept for manual or automatic replay.</summary>
public class DeadLetter
{
    public long NotificationId { get; set; }
    public string Payload { get; set; } = "";
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime LastAttempt { get; set; } = DateTime.UtcNow;
}

/// <summary>Idempotency ledger: a notification id is recorded once, so replays never double-process.</summary>
public class ProcessedNotification
{
    public long NotificationId { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Key/value state, e.g. the Ethos consume checkpoint.</summary>
public class SyncState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public const string CheckpointKey = "ethos:lastProcessedId";

    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();
    public DbSet<ProcessedNotification> ProcessedNotifications => Set<ProcessedNotification>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeadLetter>().HasKey(d => d.NotificationId);
        b.Entity<ProcessedNotification>().HasKey(p => p.NotificationId);
        b.Entity<SyncState>().HasKey(s => s.Key);
        b.Entity<SyncLog>().HasIndex(l => l.Timestamp);
        b.Entity<SyncLog>().Property(l => l.Status).HasConversion<string>();
    }
}
