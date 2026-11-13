using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampusSync.Hub.Ethos;

// ---------- EEDM-style models (only the fields we use) ----------
public record EthosNotification(
    long Id, DateTime Published, EthosResourceRef Resource, string Operation, string ContentType, JsonElement Content);
public record EthosResourceRef(string Name, Guid Id, string? Version);

public record EthosPerson(Guid Id, List<PersonName> Names, List<PersonEmail>? Emails, List<Credential>? Credentials)
{
    public string FullName => Names.FirstOrDefault(n => n.Type?.Category == "legal")?.FullName ?? Names.First().FullName;
    public string? SchoolEmail => Emails?.FirstOrDefault(e => e.Type?.EmailType == "school")?.Address ?? Emails?.FirstOrDefault()?.Address;
    public string StudentId => Credentials?.FirstOrDefault(c => c.Type == "bannerId")?.Value ?? Id.ToString();
}
public record PersonName(NameKind? Type, string FirstName, string LastName, string FullName);
public record NameKind(string Category);
public record PersonEmail(EmailKind? Type, string Address);
public record EmailKind(string EmailType);
public record Credential(string Type, string Value);

public record EthosSection(Guid Id, string Code, List<TitleValue> Titles, IdRef Course, string? StartOn)
{
    public string Title => Titles.FirstOrDefault()?.Value ?? Code;
}
public record TitleValue(string Value);
public record IdRef(Guid Id);

public record SectionRegistration(Guid Id, IdRef Registrant, IdRef Section, RegistrationStatusRef? Status);
public record RegistrationStatusRef(string RegistrationStatus);

public interface IEthosClient
{
    Task<IReadOnlyList<EthosNotification>> ConsumeAsync(long lastProcessedId, int limit, CancellationToken ct);
    Task<T> GetAsync<T>(string resource, Guid id, CancellationToken ct);
}

/// <summary>
/// Singleton token cache. Exchanges the API key for a JWT via POST /auth and reuses it until
/// shortly before expiry, exactly like the real Ethos flow. It must be a singleton: typed
/// HttpClients are transient, so a per-client cache would request a new token on every poll.
/// </summary>
public class EthosTokenProvider(IHttpClientFactory httpFactory, IConfiguration config, ILogger<EthosTokenProvider> log)
{
    public const string HttpClientName = "ethos-auth";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTime _expiresUtc;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _expiresUtc) return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _expiresUtc) return _token;
            using var req = new HttpRequestMessage(HttpMethod.Post, "auth");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Ethos:ApiKey"]);
            using var res = await httpFactory.CreateClient(HttpClientName).SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            _token = (await res.Content.ReadAsStringAsync(ct)).Trim();
            _expiresUtc = DateTime.UtcNow.AddMinutes(4); // Ethos tokens live 5 min; refresh early
            log.LogInformation("Obtained new Ethos access token, valid until {ExpiresUtc:HH:mm:ss}Z", _expiresUtc);
            return _token;
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Talks to Ethos Integration: change-notification feed and EEDM resources.</summary>
public class EthosClient(HttpClient http, EthosTokenProvider tokens) : IEthosClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<HttpRequestMessage> BuildAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
        req.Headers.Accept.ParseAdd("application/vnd.hedtech.integration.v12+json");
        return req;
    }

    public async Task<IReadOnlyList<EthosNotification>> ConsumeAsync(long lastProcessedId, int limit, CancellationToken ct)
    {
        using var req = await BuildAsync($"consume?lastProcessedID={lastProcessedId}&limit={limit}", ct);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<EthosNotification>>(Json, ct) ?? [];
    }

    public async Task<T> GetAsync<T>(string resource, Guid id, CancellationToken ct)
    {
        using var req = await BuildAsync($"api/{resource}/{id}", ct);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
