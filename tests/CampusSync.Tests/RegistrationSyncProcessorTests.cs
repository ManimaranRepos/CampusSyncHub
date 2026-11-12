using System.Text.Json;
using CampusSync.Hub.Data;
using CampusSync.Hub.Ethos;
using CampusSync.Hub.Sync;
using CampusSync.Hub.Targets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampusSync.Tests;

public class RegistrationSyncProcessorTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly TestDbFactory _db;
    private readonly FakeEthos _ethos = new();
    private readonly FakeTargets _targets = new();

    public RegistrationSyncProcessorTests()
    {
        _conn.Open();
        _db = new TestDbFactory(new DbContextOptionsBuilder<SyncDbContext>().UseSqlite(_conn).Options);
        using var ctx = _db.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    private RegistrationSyncProcessor Sut() =>
        new(_db, _ethos, _targets, new SyncNotifier(), NullLogger<RegistrationSyncProcessor>.Instance);

    [Fact]
    public async Task Registration_is_synced_to_lms_library_and_email()
    {
        var outcome = await Sut().ProcessAsync(_ethos.Notification(1), isReplay: false, default);

        Assert.Equal(ProcessOutcome.Synced, outcome);
        Assert.Equal(["LMS:B100200:CS310-01", "LIB:B100200", "MAIL:priya.raman@demo-university.edu"], _targets.Calls);
        using var ctx = _db.CreateDbContext();
        var log = Assert.Single(ctx.SyncLogs);
        Assert.Equal(SyncStatus.Success, log.Status);
        Assert.Equal("Priya Raman", log.StudentName);
    }

    [Fact]
    public async Task Same_notification_is_never_processed_twice()
    {
        var sut = Sut();
        await sut.ProcessAsync(_ethos.Notification(7), false, default);
        var second = await sut.ProcessAsync(_ethos.Notification(7), false, default);

        Assert.Equal(ProcessOutcome.Duplicate, second);
        Assert.Equal(3, _targets.Calls.Count); // only the first run hit downstream systems
    }

    [Fact]
    public async Task Downstream_failure_goes_to_dead_letter_and_replay_clears_it()
    {
        _targets.FailLms = true;
        var sut = Sut();

        Assert.Equal(ProcessOutcome.Failed, await sut.ProcessAsync(_ethos.Notification(3), false, default));
        using (var ctx = _db.CreateDbContext())
            Assert.Equal(1, Assert.Single(ctx.DeadLetters).Attempts);

        _targets.FailLms = false;
        Assert.Equal(ProcessOutcome.Synced, await sut.ReplayAsync(3, default));

        using var after = _db.CreateDbContext();
        Assert.Empty(after.DeadLetters);
        Assert.Contains(after.SyncLogs, l => l.Status == SyncStatus.Retried);
    }

    [Fact]
    public async Task Non_registration_notifications_are_ignored()
    {
        var n = _ethos.Notification(9) with { Resource = new EthosResourceRef("persons", Guid.NewGuid(), null) };
        Assert.Equal(ProcessOutcome.Ignored, await Sut().ProcessAsync(n, false, default));
        Assert.Empty(_targets.Calls);
    }

    public void Dispose() => _conn.Dispose();

    // ---------- fakes ----------
    private class TestDbFactory(DbContextOptions<SyncDbContext> o) : IDbContextFactory<SyncDbContext>
    {
        public SyncDbContext CreateDbContext() => new(o);
    }

    private class FakeEthos : IEthosClient
    {
        private readonly Guid _person = Guid.NewGuid(), _section = Guid.NewGuid();

        public EthosNotification Notification(long id)
        {
            var reg = new SectionRegistration(Guid.NewGuid(), new IdRef(_person), new IdRef(_section), new RegistrationStatusRef("registered"));
            return new EthosNotification(id, DateTime.UtcNow, new EthosResourceRef("section-registrations", reg.Id, null),
                "created", "resource-representation", JsonSerializer.SerializeToElement(reg, EthosClient.Json));
        }

        public Task<IReadOnlyList<EthosNotification>> ConsumeAsync(long lastProcessedId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EthosNotification>>([]);

        public Task<T> GetAsync<T>(string resource, Guid id, CancellationToken ct) => Task.FromResult((T)(object)(resource switch
        {
            "persons" => new EthosPerson(id, [new PersonName(new NameKind("legal"), "Priya", "Raman", "Priya Raman")],
                [new PersonEmail(new EmailKind("school"), "priya.raman@demo-university.edu")], [new Credential("bannerId", "B100200")]),
            "sections" => new EthosSection(id, "CS310-01", [new TitleValue("Database Systems")], new IdRef(Guid.NewGuid()), "2027-01-11"),
            _ => throw new NotSupportedException(resource)
        }));
    }

    private class FakeTargets : ITargetGateway
    {
        public List<string> Calls { get; } = [];
        public bool FailLms { get; set; }

        public Task EnrollInLmsAsync(string studentId, string studentName, string sectionCode, string courseTitle, CancellationToken ct)
        {
            if (FailLms) throw new HttpRequestException("LMS returned 503 Service Unavailable");
            Calls.Add($"LMS:{studentId}:{sectionCode}");
            return Task.CompletedTask;
        }
        public Task EnsureLibraryAccountAsync(string studentId, string fullName, string email, CancellationToken ct)
        { Calls.Add($"LIB:{studentId}"); return Task.CompletedTask; }
        public Task SendEmailAsync(string to, string subject, string body, CancellationToken ct)
        { Calls.Add($"MAIL:{to}"); return Task.CompletedTask; }
    }
}
