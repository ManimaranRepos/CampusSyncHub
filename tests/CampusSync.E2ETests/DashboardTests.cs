using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CampusSync.E2ETests;

/// <summary>
/// Drives the real Blazor dashboard in a browser against the full running system.
/// Tests assert relative changes, so they pass in any order on the shared stack.
/// </summary>
[Collection("stack")]
public class DashboardTests(CampusSyncStack stack) : IAsyncLifetime
{
    private static readonly LocatorAssertionsToHaveTextOptions Slow = new() { Timeout = 30_000 };
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        _context = await stack.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
        _page = await _context.NewPageAsync();
        await _page.GotoAsync(stack.HubUrl);
        // Wait until the interactive (SignalR) circuit is up, so buttons respond.
        await _page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await Expect(_page.Locator(".worker small")).Not.ToContainTextAsync("Starting", new() { Timeout = 30_000 });
    }

    private ILocator Kpi(string label) => _page.Locator(".kpi", new() { HasText = label }).Locator("b");
    private async Task<int> KpiValueAsync(string label) => int.Parse((await Kpi(label).InnerTextAsync()).TrimStart('#'));

    [Fact]
    public async Task Seeded_ethos_registrations_are_synced_to_every_downstream_system()
    {
        await Expect(Kpi("Synced successfully")).ToHaveTextAsync(new Regex(@"^([5-9]|\d{2,})$"), Slow);

        var firstRow = _page.Locator("tbody tr").First;
        await Expect(firstRow).ToContainTextAsync("LMS ✓ · Library ✓ · Email ✓");
        await Expect(firstRow.Locator(".pill")).ToHaveTextAsync(new Regex("Success|Retried"));
        await Expect(_page.Locator(".down li", new() { HasText = "LMS enrollments" }).Locator("b"))
            .Not.ToHaveTextAsync(new Regex("^(0|–|offline)$"));
    }

    [Fact]
    public async Task New_registration_appears_live_without_page_refresh()
    {
        await Expect(Kpi("Ethos checkpoint")).Not.ToHaveTextAsync("#0", Slow);
        var before = await KpiValueAsync("Notifications processed");

        await _page.ClickAsync("text=+ Simulate a student registration");

        // No reload: the SignalR circuit pushes the new state into the open page.
        await Expect(Kpi("Notifications processed")).ToHaveTextAsync((before + 1).ToString(), Slow);
        await Expect(_page.Locator("tbody tr").First).ToContainTextAsync("Email ✓");
    }

    [Fact]
    public async Task Outage_moves_registration_to_dead_letter_queue_and_retry_recovers_it()
    {
        await Expect(Kpi("Dead-letter queue")).ToHaveTextAsync("0", Slow);
        await stack.StopTargetsAsync();
        try
        {
            await _page.ClickAsync("text=+ Simulate a student registration");

            // Polly retries, gives up, and the processor parks the event instead of losing it.
            await Expect(Kpi("Dead-letter queue")).ToHaveTextAsync("1", Slow);
            await Expect(_page.Locator(".dead")).ToContainTextAsync("attempt");
            await Expect(_page.Locator("tbody tr").First.Locator(".pill")).ToHaveTextAsync("Failed");
        }
        finally
        {
            await stack.StartTargetsAsync();
        }

        await _page.ClickAsync(".dead button:has-text('Retry')");

        await Expect(Kpi("Dead-letter queue")).ToHaveTextAsync("0", Slow);
        await Expect(_page.Locator(".msg")).ToContainTextAsync("Synced");
        await Expect(_page.Locator("tbody tr").First.Locator(".pill")).ToHaveTextAsync("Retried");
    }

    [Fact]
    public async Task Stats_api_matches_the_dashboard()
    {
        await Expect(Kpi("Ethos checkpoint")).Not.ToHaveTextAsync("#0", Slow);
        var response = await _page.APIRequest.GetAsync($"{stack.HubUrl}/api/stats");
        Assert.True(response.Ok);
        var json = await response.JsonAsync();
        Assert.True(json!.Value.GetProperty("processed").GetInt32() >= 5);
        Assert.Equal(0, json.Value.GetProperty("deadLetters").GetInt32());
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();
}
