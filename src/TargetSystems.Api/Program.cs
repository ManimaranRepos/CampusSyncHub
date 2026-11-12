// Mock downstream campus systems: LMS, Library, Notification gateway.
// CHAOS_RATE (0.0 - 1.0) makes a share of requests fail with 503 so the Hub's
// retry / dead-letter behaviour can be demonstrated.
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var chaosRate = double.TryParse(app.Configuration["CHAOS_RATE"], out var r) ? r : 0.0;
var lms = new ConcurrentDictionary<string, Enrollment>();
var library = new ConcurrentDictionary<string, LibraryAccount>();
var outbox = new ConcurrentQueue<EmailMessage>();

bool Chaos() => Random.Shared.NextDouble() < chaosRate;

// ----- LMS -----
// Idempotent: same (student, course) enrollment returns 200 instead of creating a duplicate.
app.MapPost("/lms/enrollments", (EnrollmentRequest req) =>
{
    if (Chaos()) return Results.StatusCode(503);
    var key = $"{req.StudentId}:{req.SectionCode}";
    var created = lms.TryAdd(key, new Enrollment(req.StudentId, req.StudentName, req.SectionCode, req.CourseTitle, DateTime.UtcNow));
    return created ? Results.Created($"/lms/enrollments/{key}", lms[key]) : Results.Ok(lms[key]);
});
app.MapGet("/lms/enrollments", () => lms.Values.OrderBy(e => e.EnrolledAt));

// ----- Library -----
app.MapPost("/library/accounts", (LibraryAccountRequest req) =>
{
    if (Chaos()) return Results.StatusCode(503);
    var account = library.GetOrAdd(req.StudentId, id => new LibraryAccount(id, req.FullName, $"LIB-{Random.Shared.Next(10000, 99999)}", DateTime.UtcNow));
    return Results.Ok(account);
});
app.MapGet("/library/accounts", () => library.Values.OrderBy(a => a.CreatedAt));

// ----- Notifications -----
app.MapPost("/notify/email", (EmailMessage msg) =>
{
    if (Chaos()) return Results.StatusCode(503);
    outbox.Enqueue(msg with { SentAt = DateTime.UtcNow });
    app.Logger.LogInformation("EMAIL to {To}: {Subject}", msg.To, msg.Subject);
    return Results.Accepted();
});
app.MapGet("/notify/outbox", () => outbox.ToArray());

app.MapGet("/", () => $"Target systems running (chaos rate {chaosRate:P0}). /lms/enrollments | /library/accounts | /notify/outbox");

app.Run();

record EnrollmentRequest(string StudentId, string StudentName, string SectionCode, string CourseTitle);
record Enrollment(string StudentId, string StudentName, string SectionCode, string CourseTitle, DateTime EnrolledAt);
record LibraryAccountRequest(string StudentId, string FullName, string Email);
record LibraryAccount(string StudentId, string FullName, string CardNumber, DateTime CreatedAt);
record EmailMessage(string To, string Subject, string Body, DateTime? SentAt = null);
