using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// /global/rl shows GLOBAL's two RL reports in a sandboxed frame, plus the page's own summary;
/// the hub links into them. GitHub images are answered locally so the run never depends on it.
/// </summary>
public class GlobalRlUiTests : IAsyncLifetime
{
    private static readonly string BaseUrl = LiveApp.BaseUrl;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private readonly List<PlaywrightErrorGuard> _guards = [];

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

    private async Task<IPage> NewPageAsync(int width = 1440, int height = 1000, bool isMobile = false)
    {
        var page = await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
        });
        await page.RouteAsync($"{BaseUrl}/**", route => route.ContinueAsync(new()
        {
            Headers = new Dictionary<string, string>(route.Request.Headers) { ["X-Fake-User"] = "browser-test" },
        }));
        await page.RouteAsync("https://raw.githubusercontent.com/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "image/png",
            BodyBytes = Png,
        }));
        await page.RouteAsync("http://127.0.0.1:5050/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
            ContentType = "application/json",
            Body = """{"ok": true, "authorized": false}""",
        }));
        _guards.Add(PlaywrightErrorGuard.Watch(page));
        return page;
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "popunkoutersoftware-e2eui-shots");
        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name) });
    }

    [LiveAppFact]
    public async Task Overview_IsTheOldComparisonAndTriageReport_InASandboxedFrame()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/global/rl", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var frame = page.Locator("iframe.global-report");
        await Assertions.Expect(frame).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await frame.GetAttributeAsync("sandbox")).Should().NotContain("allow-same-origin");
        var report = page.FrameLocator("iframe.global-report");
        await Assertions.Expect(report.GetByRole(AriaRole.Heading, new() { NameRegex = new("What Should I Train Next") }).First)
            .ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(report.GetByRole(AriaRole.Heading, new() { NameRegex = new("Can I Move This Agent") }).First)
            .ToBeAttachedAsync();
        await CaptureAsync(page, "global-rl-overview-desktop.png");
    }

    [LiveAppFact]
    public async Task Progress_And_Summary_ViewsSwitchFromTheChips()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/global/rl", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await page.GetByRole(AriaRole.Link, new() { Name = "Progress over time", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("view=progress"));
        var report = page.FrameLocator("iframe.global-report");
        await Assertions.Expect(report.GetByRole(AriaRole.Heading, new() { NameRegex = new("RL Agent Progress Over Time") }).First)
            .ToBeVisibleAsync(new() { Timeout = 40_000 });

        await page.GetByRole(AriaRole.Link, new() { Name = "Summary", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("iframe.global-report")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("peak env-steps/s").First).ToBeVisibleAsync(new() { Timeout = 40_000 });
    }

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Hub_LinksIntoTheReportsAndStandards_WithoutSidewaysScroll(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/global", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Project groups & reports" })).ToBeVisibleAsync(new() { Timeout = 40_000 });
        foreach (var href in new[] { "/global/rl?view=overview", "/global/rl?view=progress", "/global/standards?open=portability-contract", "/global/standards?open=blazor-prompt" })
            await Assertions.Expect(page.Locator($"a[href='{href}']")).ToHaveCountAsync(1);
        var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(1, $"the hub must not scroll sideways at {label}");
        await CaptureAsync(page, $"global-hub-{label}.png");

        await page.Locator("a[href='/global/standards?open=portability-contract']").ClickAsync();
        await Assertions.Expect(page.Locator("details#portability-contract[open]")).ToBeVisibleAsync(new() { Timeout = 40_000 });
    }
}
