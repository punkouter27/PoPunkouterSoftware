using System.Text.Json;
using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// The owner's /global pages in a real browser, including the buttons that act on the PC.
/// </summary>
/// <remarks>
/// The local helper (scripts/global/local_helper.py, 127.0.0.1:5050) is stood in for by a
/// Playwright route, so nothing is ever started on the machine running the tests: the tests
/// prove that the page finds the helper, shows the PC buttons only when it is connected, and
/// sends job ids and hours (never a command). The helper's own rules are covered by
/// scripts/global/test_local_helper.py.
/// </remarks>
public class GlobalUiTests : IAsyncLifetime
{
    private const string HelperToken = "browser-test-token-0123456789abcdef";
    private static readonly string BaseUrl = LiveApp.BaseUrl;

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private readonly List<PlaywrightErrorGuard> _guards = [];
    private readonly List<JsonElement> _actions = [];
    private readonly List<string?> _tokens = [];

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

    private enum Helper
    {
        Connected,
        NeedsToken,
    }

    /// <summary>A signed-in page whose calls to 127.0.0.1:5050 are answered here, not by a real helper.</summary>
    private async Task<IPage> NewPageAsync(Helper helper, int width = 1440, int height = 1000, bool isMobile = false)
    {
        var page = await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
        });
        // Same-origin requests only: the fake-user header must not be sent to the helper address.
        await page.RouteAsync($"{BaseUrl}/**", route => route.ContinueAsync(new()
        {
            Headers = new Dictionary<string, string>(route.Request.Headers) { ["X-Fake-User"] = "browser-test" },
        }));
        if (helper == Helper.Connected)
            await page.AddInitScriptAsync($"localStorage.setItem('po.localHelper.token', '{HelperToken}')");

        await page.RouteAsync("http://127.0.0.1:5050/**", async route =>
        {
            var cors = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" };
            var token = await route.Request.HeaderValueAsync("x-po-helper-token");
            var authorized = token == HelperToken;
            if (route.Request.Method == "GET")
            {
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    Headers = cors,
                    ContentType = "application/json",
                    Body = authorized
                        ? """{"ok": true, "authorized": true, "output": "C:\\work\\_CODEMATT"}"""
                        : """{"ok": true, "authorized": false}""",
                });
                return;
            }

            _tokens.Add(token);
            _actions.Add(JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement.Clone());
            await route.FulfillAsync(new()
            {
                Status = authorized ? 200 : 401,
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

    [LiveAppFact]
    public async Task Header_ShowsTheOwnerLinks_AndTheHubListsEveryTool()
    {
        var page = await NewPageAsync(Helper.Connected);
        await page.GotoAsync($"{BaseUrl}/global", new() { WaitUntil = WaitUntilState.NetworkIdle });

        foreach (var (label, href) in new[] { ("Azure", "/azure"), ("Users", "/users"), ("Fleet & training", "/global") })
            await Assertions.Expect(page.Locator($".po-header a.po-header__link[href='{href}']")).ToHaveTextAsync(label);

        var tiles = page.Locator("a.global-tile");
        await Assertions.Expect(tiles.First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        foreach (var href in new[] { "/global/fleet", "/global/queue", "/global/pc", "/global/standards", "/global/rl" })
            await Assertions.Expect(page.Locator($"a.global-tile[href='{href}']")).ToHaveCountAsync(1);
        await CaptureAsync(page, "global-hub-desktop.png");
    }

    [LiveAppFact]
    public async Task Queue_Connected_LaunchSendsIdsAndHours_NeverACommand()
    {
        var page = await NewPageAsync(Helper.Connected);
        await page.GotoAsync($"{BaseUrl}/global/queue", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        var firstReady = page.Locator(".global-list .global-item input[type=checkbox]:not([disabled])").First;
        await firstReady.CheckAsync();
        await CaptureAsync(page, "global-queue-desktop.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Dry run (starts nothing)" }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");

        var sent = _actions.Should().ContainSingle().Subject;
        sent.GetProperty("action").GetString().Should().Be("launch_queue");
        sent.GetProperty("dry").GetBoolean().Should().BeTrue();
        var job = sent.GetProperty("jobs").EnumerateArray().Should().ContainSingle().Subject;
        job.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        job.GetProperty("hours").GetDouble().Should().BeGreaterThan(0);
        sent.GetRawText().Should().NotContain("command", "the helper takes every command from its own catalog");
        _tokens.Should().OnlyContain(t => t == HelperToken);

        // A real start is two clicks: the first only arms the confirmation.
        await page.GetByRole(AriaRole.Button, new() { Name = "Start on this PC…" }).ClickAsync();
        _actions.Should().HaveCount(1, "arming the confirmation sends nothing");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Yes, start 1 job") })).ToBeVisibleAsync();
    }

    [LiveAppFact]
    public async Task Queue_CloudRun_NeedsAConfirmationThatNamesTheCost()
    {
        var page = await NewPageAsync(Helper.Connected);
        await page.GotoAsync($"{BaseUrl}/global/queue", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var send = page.GetByRole(AriaRole.Button, new() { Name = "Send to the cloud GPU…" }).First;
        await Assertions.Expect(send).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await send.ClickAsync();
        _actions.Should().BeEmpty("the first click only asks");

        var confirm = page.GetByRole(AriaRole.Button, new() { NameRegex = new("costs money") });
        await Assertions.Expect(confirm).ToBeVisibleAsync();
        await confirm.ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");

        var sent = _actions.Should().ContainSingle().Subject;
        sent.GetProperty("action").GetString().Should().Be("cloud");
        sent.GetProperty("command").GetString().Should().Be("run");
        sent.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        sent.TryGetProperty("dry", out var dry).Should().BeTrue();
        dry.GetBoolean().Should().BeFalse();
    }

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Queue_WithoutTheToken_ListsJobsButOffersNoWayToStartThem(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(Helper.NeedsToken, width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/global/queue", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.Locator(".global-list .global-item").First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(page.GetByText("Helper found")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#local-helper-token")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start on this PC…" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Send to the cloud GPU…" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".global-list input[type=checkbox]")).ToHaveCountAsync(0);
        var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(1, $"the queue page must not scroll sideways at {label}");
        await CaptureAsync(page, $"global-queue-notoken-{label}.png");

        // Pasting the token connects without a reload.
        await page.Locator("#local-helper-token").FillAsync(HelperToken);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect" }).ClickAsync();
        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start on this PC…" })).ToBeVisibleAsync();
    }

    [LiveAppFact]
    public async Task Fleet_Connected_RowButtonsNameTheRepo()
    {
        var page = await NewPageAsync(Helper.Connected);
        await page.GotoAsync($"{BaseUrl}/global/fleet", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var status = page.GetByRole(AriaRole.Button, new() { Name = "Status", Exact = true }).First;
        await Assertions.Expect(status).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await status.ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");

        var sent = _actions.Should().ContainSingle().Subject;
        sent.GetProperty("action").GetString().Should().Be("git_status");
        sent.GetProperty("repo").GetString().Should().StartWith("Po");
        await CaptureAsync(page, "global-fleet-desktop.png");
    }

    [LiveAppFact]
    public async Task ThisPc_Connected_OffersRefreshBenchmarksImportAndCloud()
    {
        var page = await NewPageAsync(Helper.Connected);
        await page.GotoAsync($"{BaseUrl}/global/pc", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        foreach (var name in new[] { "Crawl only", "Crawl, commit and push", "Check fit (copies nothing)", "Status", "Stop the VM", "Start the VM…" })
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Run all RL repos") })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        // The pickers are filled from the fleet snapshot: RL repos to benchmark, brains to copy.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "PoBox", Exact = true })).ToBeVisibleAsync();
        (await page.Locator("#pc-brains option").CountAsync()).Should().BeGreaterThan(10);

        await page.GetByRole(AriaRole.Button, new() { Name = "Status", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        _actions.Should().ContainSingle().Which.GetProperty("command").GetString().Should().Be("status");
        await CaptureAsync(page, "global-pc-desktop.png");
    }

    [LiveAppFact]
    public async Task Standards_RendersTheDocuments()
    {
        var page = await NewPageAsync(Helper.NeedsToken);
        await page.GotoAsync($"{BaseUrl}/global/standards", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var docs = page.Locator("details.global-item");
        await Assertions.Expect(docs.First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await docs.CountAsync()).Should().BeGreaterThanOrEqualTo(4);
        await docs.First.Locator("summary").ClickAsync();
        await Assertions.Expect(docs.First.Locator(".global-doc")).ToBeVisibleAsync();
        await CaptureAsync(page, "global-standards-desktop.png");
    }
}
