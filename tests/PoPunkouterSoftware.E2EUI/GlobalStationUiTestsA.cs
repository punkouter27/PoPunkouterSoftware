using System.Text.Json;
using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// Fleet Health, Briefing, Disk and Lessons, ported from GLOBAL's station pages, in a real browser.
/// The local helper is a Playwright route (as in <see cref="GlobalUiTests"/>), so nothing ever runs
/// on the machine: "connected" answers like the helper, "offline" answers that no helper is there.
/// </summary>
public class GlobalStationUiTestsA : IAsyncLifetime
{
    private const string HelperToken = "browser-test-token-0123456789abcdef";
    private const string Workspace = @"C:\work\_CODEMATT";
    private static readonly string BaseUrl = LiveApp.BaseUrl;

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private readonly List<PlaywrightErrorGuard> _guards = [];
    private readonly List<JsonElement> _actions = [];

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL"),
        });
    }

    public async Task DisposeAsync()
    {
        foreach (var guard in _guards)
            await guard.DisposeAsync();
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    private async Task<IPage> NewPageAsync(bool connected, int width = 1440, int height = 1000, bool isMobile = false)
    {
        var page = await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
            Permissions = ["clipboard-read", "clipboard-write"],
        });
        await page.RouteAsync($"{BaseUrl}/**", route => route.ContinueAsync(new()
        {
            Headers = new Dictionary<string, string>(route.Request.Headers) { ["X-Fake-User"] = "browser-test" },
        }));
        if (connected)
            await page.AddInitScriptAsync($"localStorage.setItem('po.localHelper.token', '{HelperToken}')");

        await page.RouteAsync("http://127.0.0.1:5050/**", async route =>
        {
            var cors = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" };
            if (!connected)
            {
                // A reply the client reads as "no helper here" without a refused request in the console.
                await route.FulfillAsync(new() { Status = 200, Headers = cors, ContentType = "application/json", Body = """{"ok": false}""" });
                return;
            }

            if (route.Request.Method == "GET")
            {
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    Headers = cors,
                    ContentType = "application/json",
                    Body = JsonSerializer.Serialize(new { ok = true, authorized = true, output = Workspace }),
                });
                return;
            }

            _actions.Add(JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement.Clone());
            await route.FulfillAsync(new()
            {
                Status = 200,
                Headers = cors,
                ContentType = "application/json",
                Body = """{"ok": true, "authorized": true, "output": "helper reply: done"}""",
            });
        });

        _guards.Add(PlaywrightErrorGuard.Watch(page));
        return page;
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "popunkoutersoftware-e2eui-shots");
        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name), FullPage = false });
    }

    private static async Task AssertNoSidewaysScrollAsync(IPage page, string label)
    {
        var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(1, $"the page must not scroll sideways at {label}");
    }

    // ─── Fleet Health ────────────────────────────────────────────────────────

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Fleet_ShowsAlertsKpisFeedAndUncommittedFiles(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(connected: false, width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/global/fleet", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("git repos tracked")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        foreach (var kpi in new[] { "repos with unpushed commits", "commits in last 7 days", "on disk (all folders)" })
            await Assertions.Expect(page.GetByText(kpi)).ToBeVisibleAsync();
        foreach (var header in new[] { "Alerts", "Branch", "Dirty", "Commits 7d/30d", "CI (latest)", "Tests" })
            await Assertions.Expect(page.GetByRole(AriaRole.Columnheader, new() { Name = header, Exact = true })).ToBeVisibleAsync();

        await Assertions.Expect(page.GetByText("Latest commits across the fleet")).ToBeVisibleAsync();
        (await page.Locator(".fleet-feed li").CountAsync()).Should().BeGreaterThan(5);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Uncommitted files" })).ToBeVisibleAsync();

        // Without the helper each row offers a copyable "code <path>" instead of the PC buttons.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "copy: code …" }).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Code", Exact = true })).ToHaveCountAsync(0);

        var all = await page.Locator(".fleet-table tbody tr").CountAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "RL games", Exact = true }).ClickAsync();
        var rl = await page.Locator(".fleet-table tbody tr").CountAsync();
        rl.Should().BeGreaterThan(0).And.BeLessThan(all);

        await page.GetByRole(AriaRole.Button, new() { Name = "Needs attention" }).ClickAsync();
        var flagged = page.Locator(".fleet-table tbody tr");
        (await flagged.CountAsync()).Should().BeGreaterThan(0);
        (await flagged.Locator("text=all clear").CountAsync()).Should().Be(0, "every row under Needs attention has an alert");

        await AssertNoSidewaysScrollAsync(page, label);
        await CaptureAsync(page, $"global-fleet-{label}.png");
    }

    [LiveAppFact]
    public async Task Fleet_CopyFallback_UsesTheWorkspacePathWhenTheHelperIsConnected()
    {
        var page = await NewPageAsync(connected: true);
        await page.GotoAsync($"{BaseUrl}/global/fleet", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Code", Exact = true }).First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "copy: code …" })).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Code", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        var sent = _actions.Should().ContainSingle().Subject;
        sent.GetProperty("action").GetString().Should().Be("open_vscode");
        sent.GetProperty("repo").GetString().Should().StartWith("Po");
    }

    // ─── Briefing ────────────────────────────────────────────────────────────

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Briefing_ShowsEverySectionOfTheOldMorningBriefing(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(connected: false, width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/global/briefing", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What changed?" })).ToBeVisibleAsync(new() { Timeout = 40_000 });
        foreach (var heading in new[] { "New brains", "RL training totals", "Other changes", "Commits", "Overnight queue runs" })
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Exact = true })).ToBeVisibleAsync();
        foreach (var kpi in new[] { "new commits", "new or changed .onnx brains", "overnight queue reports" })
            await Assertions.Expect(page.GetByText(kpi, new() { Exact = true })).ToBeVisibleAsync();

        await AssertNoSidewaysScrollAsync(page, label);
        await CaptureAsync(page, $"global-briefing-{label}.png");
    }

    // ─── Disk ────────────────────────────────────────────────────────────────

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Disk_DrawsStackedBarsTrendAndFiltersHeavyFolders(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(connected: false, width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/global/disk", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("safe to reclaim (rebuilt automatically)")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await page.Locator(".disk-row").CountAsync()).Should().BeGreaterThan(5);
        (await page.Locator(".disk-seg").CountAsync()).Should().BeGreaterThan(10);
        (await page.Locator(".disk-legend li").CountAsync()).Should().Be(8);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Total over time" })).ToBeVisibleAsync();

        await page.GetByText("Show as a table").ClickAsync();
        await Assertions.Expect(page.Locator(".disk-details table")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Safe", Exact = true }).ClickAsync();
        var badges = await page.Locator("table:has(th:text('Cleanup')) tbody td .global-tone").AllInnerTextsAsync();
        badges.Should().NotBeEmpty().And.OnlyContain(b => b.Contains("Safe — rebuilt automatically"));

        await AssertNoSidewaysScrollAsync(page, label);
        await CaptureAsync(page, $"global-disk-{label}.png");
    }

    [LiveAppFact]
    public async Task Disk_CopyCommand_PutsAFullPathCommandOnTheClipboardAndDeletesNothing()
    {
        var page = await NewPageAsync(connected: true);
        await page.GotoAsync($"{BaseUrl}/global/disk", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy command" }).First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Safe", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy command" }).First.ClickAsync();

        await Assertions.Expect(page.GetByText("Nothing has been deleted")).ToBeVisibleAsync();
        var clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        clipboard.Should().StartWith("Remove-Item -LiteralPath \"" + Workspace + "\\");
        _actions.Should().BeEmpty("copying a command never calls the helper");
    }

    // ─── Lessons ─────────────────────────────────────────────────────────────

    [LiveAppFact]
    public async Task Lessons_TagsFilterTogether_AndSourcesLinkToGitHub()
    {
        var page = await NewPageAsync(connected: false);
        await page.GotoAsync($"{BaseUrl}/global/lessons", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var items = page.Locator(".global-list > li");
        await Assertions.Expect(items.First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await page.Locator(".global-list a[href^='https://github.com/']").CountAsync()).Should().BeGreaterThan(0);

        var count = page.Locator(".global-toolbar .global-count").First;
        var before = await count.InnerTextAsync();
        await page.Locator(".lessons-tag").First.ClickAsync();
        await Assertions.Expect(count).Not.ToHaveTextAsync(before);
        await Assertions.Expect(page.Locator(".lessons-tag.is-on")).ToHaveCountAsync(1);
        await CaptureAsync(page, "global-lessons-desktop.png");
    }

    [LiveAppFact]
    public async Task Lessons_HashPrefillsRepoAndSearch()
    {
        var page = await NewPageAsync(connected: false);
        await page.GotoAsync($"{BaseUrl}/global/lessons#repo=PoBox&q=onnx", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.Locator("input[type=search]")).ToHaveValueAsync("onnx", new() { Timeout = 40_000 });
        await Assertions.Expect(page.Locator("select[aria-label='Filter by repo']")).ToHaveValueAsync("PoBox");
        var metas = await page.Locator(".global-list > li .meta").AllInnerTextsAsync();
        metas.Should().NotBeEmpty().And.OnlyContain(m => m.Contains("PoBox"));
    }
}
