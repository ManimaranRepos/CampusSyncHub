namespace CampusSync.Hub.Targets;

public interface ITargetGateway
{
    Task EnrollInLmsAsync(string studentId, string studentName, string sectionCode, string courseTitle, CancellationToken ct);
    Task EnsureLibraryAccountAsync(string studentId, string fullName, string email, CancellationToken ct);
    Task SendEmailAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// One typed HttpClient for the downstream systems. Retries / timeouts / circuit breaking
/// come from the resilience pipeline registered in Program.cs, not from code here.
/// </summary>
public class TargetGateway(HttpClient http) : ITargetGateway
{
    public async Task EnrollInLmsAsync(string studentId, string studentName, string sectionCode, string courseTitle, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("lms/enrollments", new { studentId, studentName, sectionCode, courseTitle }, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task EnsureLibraryAccountAsync(string studentId, string fullName, string email, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("library/accounts", new { studentId, fullName, email }, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("notify/email", new { to, subject, body }, ct);
        res.EnsureSuccessStatusCode();
    }
}
