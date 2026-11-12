// Mock Ellucian Ethos Integration API
// Mirrors the real Ethos contract closely enough that the Hub can be pointed at a
// real tenant (https://integrate.elluciancloud.com) by changing configuration only:
//   POST /auth                      API key (Bearer) -> short-lived JWT
//   GET  /api/{resource}[/{id}]     EEDM-style resources (persons, courses, sections, section-registrations)
//   GET  /consume?lastProcessedID=  change-notification feed
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apiKey = app.Configuration["Ethos:ApiKey"] ?? "demo-api-key-123";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("mock-ethos-signing-key-please-change-0123456789"));
var store = new EthosStore();
store.Seed();

// ---------- Auth ----------
app.MapPost("/auth", (HttpRequest req) =>
{
    var header = req.Headers.Authorization.ToString();
    if (header != $"Bearer {apiKey}") return Results.Unauthorized();

    var token = new JwtSecurityToken(
        issuer: "mock-ethos",
        claims: [new Claim("tenant", "demo-university")],
        expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    // Real Ethos returns the JWT as plain text, so do we.
    return Results.Text(new JwtSecurityTokenHandler().WriteToken(token), "text/plain");
});

bool Authorized(HttpRequest req)
{
    var header = req.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ")) return false;
    try
    {
        new JwtSecurityTokenHandler().ValidateToken(header[7..], new TokenValidationParameters
        {
            ValidIssuer = "mock-ethos", ValidateAudience = false, IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.Zero
        }, out _);
        return true;
    }
    catch { return false; }
}

// ---------- Resources ----------
string[] resources = ["persons", "courses", "sections", "section-registrations"];

app.MapGet("/api/{resource}", (string resource, HttpRequest req, HttpResponse res) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    if (!resources.Contains(resource)) return Results.NotFound();
    var items = store.All(resource);
    res.Headers["x-total-count"] = items.Count.ToString();
    res.Headers["x-media-type"] = "application/vnd.hedtech.integration.v12+json";
    return Results.Json(items);
});

app.MapGet("/api/{resource}/{id:guid}", (string resource, Guid id, HttpRequest req, HttpResponse res) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    var item = store.Get(resource, id);
    res.Headers["x-media-type"] = "application/vnd.hedtech.integration.v12+json";
    return item is null ? Results.NotFound() : Results.Json(item);
});

// ---------- Change notifications ----------
app.MapGet("/consume", (HttpRequest req, long? lastProcessedID, int? limit) =>
{
    if (!Authorized(req)) return Results.Unauthorized();
    var batch = store.Notifications
        .Where(n => n["id"]!.GetValue<long>() > (lastProcessedID ?? 0))
        .Take(Math.Clamp(limit ?? 20, 1, 100))
        .ToList();
    return Results.Json(batch);
});

// ---------- Demo helpers (not part of real Ethos) ----------
app.MapPost("/simulate/registration", () =>
{
    var notification = store.CreateRandomRegistration();
    return Results.Json(notification);
});

app.MapGet("/", () => Results.Text(
    "Mock Ellucian Ethos API is running.\nPOST /auth | GET /api/{persons|courses|sections|section-registrations} | GET /consume | POST /simulate/registration"));

app.Run();

// =====================================================================

class EthosStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, JsonObject>> _data = new();
    private readonly List<JsonObject> _notifications = [];
    private readonly object _lock = new();
    private long _nextNotificationId = 1;
    private readonly Random _rng = new(42);

    public IReadOnlyList<JsonObject> Notifications { get { lock (_lock) return _notifications.ToList(); } }

    public List<JsonObject> All(string resource) =>
        _data.TryGetValue(resource, out var d) ? d.Values.ToList() : [];

    public JsonObject? Get(string resource, Guid id) =>
        _data.TryGetValue(resource, out var d) && d.TryGetValue(id, out var o) ? o : null;

    private void Put(string resource, Guid id, JsonObject obj) =>
        _data.GetOrAdd(resource, _ => new())[id] = obj;

    public void Seed()
    {
        string[] first = ["Arun", "Priya", "Karthik", "Divya", "Vignesh", "Meena", "Rahul", "Aisha", "Sanjay", "Lakshmi",
                          "Omar", "Fatima", "Deepak", "Kavya", "Hari", "Nisha", "Ravi", "Sara", "Manoj", "Anjali"];
        string[] last = ["Kumar", "Raman", "Sundar", "Iyer", "Natarajan", "Khan", "Pillai", "Reddy", "Das", "Menon"];
        for (var i = 0; i < 20; i++)
        {
            var id = Guid.NewGuid();
            var (f, l) = (first[i], last[i % last.Length]);
            Put("persons", id, new JsonObject
            {
                ["id"] = id,
                ["names"] = new JsonArray(new JsonObject
                {
                    ["type"] = new JsonObject { ["category"] = "legal" },
                    ["firstName"] = f, ["lastName"] = l, ["fullName"] = $"{f} {l}"
                }),
                ["emails"] = new JsonArray(new JsonObject
                {
                    ["type"] = new JsonObject { ["emailType"] = "school" },
                    ["address"] = $"{f.ToLower()}.{l.ToLower()}@demo-university.edu"
                }),
                ["credentials"] = new JsonArray(new JsonObject { ["type"] = "bannerId", ["value"] = $"B{100200 + i}" })
            });
        }

        (string code, string title)[] courses =
        [
            ("CS101", "Introduction to Programming"), ("CS205", "Data Structures"), ("CS310", "Database Systems"),
            ("CS350", "Cloud Computing"), ("IT220", "Web Development with .NET"), ("MA201", "Discrete Mathematics")
        ];
        foreach (var (code, title) in courses)
        {
            var courseId = Guid.NewGuid();
            Put("courses", courseId, new JsonObject
            {
                ["id"] = courseId, ["subject"] = new JsonObject { ["abbreviation"] = code[..2] },
                ["number"] = code[2..], ["titles"] = new JsonArray(new JsonObject { ["value"] = title })
            });
            var sectionId = Guid.NewGuid();
            Put("sections", sectionId, new JsonObject
            {
                ["id"] = sectionId, ["code"] = $"{code}-01", ["number"] = "01",
                ["titles"] = new JsonArray(new JsonObject { ["value"] = title }),
                ["course"] = new JsonObject { ["id"] = courseId },
                ["startOn"] = "2027-01-11", ["endOn"] = "2027-05-07"
            });
        }

        // Pre-existing notifications so the Hub has work on first start.
        for (var i = 0; i < 5; i++) CreateRandomRegistration();
    }

    public JsonObject CreateRandomRegistration()
    {
        var persons = All("persons");
        var sections = All("sections");
        var person = persons[_rng.Next(persons.Count)];
        var section = sections[_rng.Next(sections.Count)];
        var regId = Guid.NewGuid();
        var registration = new JsonObject
        {
            ["id"] = regId,
            ["registrant"] = new JsonObject { ["id"] = person["id"]!.GetValue<Guid>() },
            ["section"] = new JsonObject { ["id"] = section["id"]!.GetValue<Guid>() },
            ["status"] = new JsonObject { ["registrationStatus"] = "registered" },
            ["statusDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
        Put("section-registrations", regId, registration);

        lock (_lock)
        {
            var notification = new JsonObject
            {
                ["id"] = _nextNotificationId++,
                ["published"] = DateTime.UtcNow.ToString("O"),
                ["resource"] = new JsonObject
                {
                    ["name"] = "section-registrations", ["id"] = regId,
                    ["version"] = "application/vnd.hedtech.integration.v12+json"
                },
                ["operation"] = "created",
                ["contentType"] = "resource-representation",
                ["content"] = registration.DeepClone()
            };
            _notifications.Add(notification);
            return notification;
        }
    }
}
