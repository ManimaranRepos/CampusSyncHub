using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;

namespace CampusSync.E2ETests;

/// <summary>
/// Starts the whole system for end-to-end tests: Mock Ethos, Target Systems and the Hub,
/// each as a real process on a free port, plus one headless browser. Shared by every test
/// in the "stack" collection and torn down once at the end.
/// </summary>
public sealed class CampusSyncStack : IAsyncLifetime
{
    private readonly string _root = FindSolutionRoot();
    private readonly string _config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
    private readonly string _tempDir = Directory.CreateTempSubdirectory("campussync-e2e-").FullName;
    private readonly List<ServiceProcess> _services = [];
    private ServiceProcess? _targets;
    private IPlaywright? _playwright;

    public string EthosUrl { get; } = $"http://127.0.0.1:{FreePort()}";
    public string TargetsUrl { get; } = $"http://127.0.0.1:{FreePort()}";
    public string HubUrl { get; } = $"http://127.0.0.1:{FreePort()}";
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _services.Add(await StartAsync("MockEthos.Api", EthosUrl));
        await StartTargetsAsync();
        _services.Add(await StartAsync("CampusSync.Hub", HubUrl, new()
        {
            ["Ethos__BaseUrl"] = EthosUrl + "/",
            ["Targets__BaseUrl"] = TargetsUrl + "/",
            ["ConnectionStrings__Sync"] = $"Data Source={Path.Combine(_tempDir, "e2e.db")}",
            ["Serilog__WriteTo__1__Args__path"] = Path.Combine(_tempDir, "logs", "hub-.json"),
            ["Sync__PollSeconds"] = "1"
        }, workingDir: Path.Combine(_root, "src", "CampusSync.Hub")));

        _playwright = await Playwright.CreateAsync();
        // Locally you can reuse an installed Chrome; CI installs Playwright's own Chromium.
        var chromePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROME_PATH");
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = string.IsNullOrEmpty(chromePath) ? null : chromePath,
            Args = ["--no-sandbox"]
        });
    }

    /// <summary>Simulates an outage of every downstream system (LMS, library, email).</summary>
    public async Task StopTargetsAsync()
    {
        if (_targets is null) return;
        await _targets.DisposeAsync();
        _services.Remove(_targets);
        _targets = null;
    }

    public async Task StartTargetsAsync()
    {
        _targets = await StartAsync("TargetSystems.Api", TargetsUrl, new() { ["CHAOS_RATE"] = "0" });
        _services.Add(_targets);
    }

    private async Task<ServiceProcess> StartAsync(string project, string url, Dictionary<string, string>? env = null, string? workingDir = null)
    {
        var dll = Path.Combine(_root, "src", project, "bin", _config, "net8.0", $"{project}.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Build the solution first ('dotnet build -c {_config}'). Missing: {dll}");

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {url}")
        {
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;

        var service = new ServiceProcess(project, Process.Start(psi)!);
        using var http = new HttpClient();
        for (var i = 0; i < 60; i++)
        {
            try { if ((await http.GetAsync(url)).IsSuccessStatusCode) return service; }
            catch (HttpRequestException) { }
            if (service.Process.HasExited) break;
            await Task.Delay(500);
        }
        var output = service.Output;
        await service.DisposeAsync();
        throw new InvalidOperationException($"{project} did not start on {url}.\n{output}");
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        foreach (var s in _services) await s.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CampusSyncHub.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("CampusSyncHub.sln not found above the test output folder.");
    }

    private sealed class ServiceProcess : IAsyncDisposable
    {
        private readonly StringBuilder _log = new();
        public ServiceProcess(string name, Process process)
        {
            Name = name; Process = process;
            process.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        public string Name { get; }
        public Process Process { get; }
        public string Output { get { lock (_log) return _log.ToString(); } }

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            Process.Dispose();
        }
    }
}

[CollectionDefinition("stack")]
public class StackCollection : ICollectionFixture<CampusSyncStack>;
