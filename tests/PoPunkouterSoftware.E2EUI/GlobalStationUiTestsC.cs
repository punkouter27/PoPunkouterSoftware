using System.Text.Json;
using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// Showroom, Ship Board and Idea Incubator in a real browser.
/// </summary>
/// <remarks>
/// GitHub is never reached: raw/media.githubusercontent.com are routed to a 1×1 PNG (or a 404
/// where a test needs a missing picture). The local helper is a Playwright route as in
/// <see cref="GlobalUiTests"/>, so saving and pushing only record what the page would send.
/// </remarks>
public class GlobalStationUiTestsC : IAsyncLifetime
{
    private const string HelperToken = "browser-test-token-0123456789abcdef";
    private static readonly string BaseUrl = LiveApp.BaseUrl;
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private readonly List<PlaywrightErrorGuard> _guards = [];
    private readonly List<JsonElement> _actions = [];
    private readonly List<string> _imageRequests = [];

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

    private async Task<(IBrowserContext Context, IPage Page)> NewPageAsync(
        bool helperConnected = true, bool imagesMissing = false, int width = 1440, int height = 1000, bool isMobile = false)
    {
        var context = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
        });
        var page = await context.NewPageAsync();
        await page.RouteAsync($"{BaseUrl}/**", route => route.ContinueAsync(new()
        {
            Headers = new Dictionary<string, string>(route.Request.Headers) { ["X-Fake-User"] = "browser-test" },
        }));
        await page.RouteAsync("https://*.githubusercontent.com/**", async route =>
        {
            _imageRequests.Add(route.Request.Url);
            if (imagesMissing)
                await route.FulfillAsync(new() { Status = 404, Body = "" });
            else
                await route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = TinyPng });
        });
        if (helperConnected)
            await page.AddInitScriptAsync($"if (!localStorage.getItem('po.localHelper.token')) localStorage.setItem('po.localHelper.token', '{HelperToken}')");

        await page.RouteAsync("http://127.0.0.1:5050/**", async route =>
        {
            var cors = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" };
            var authorized = await route.Request.HeaderValueAsync("x-po-helper-token") == HelperToken;
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

            _actions.Add(JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement.Clone());
            await route.FulfillAsync(new()
            {
                Status = authorized ? 200 : 401,
                Headers = cors,
                ContentType = "application/json",
                Body = """{"ok": true, "authorized": true, "output": "helper reply: done"}""",
            });
        });

        // The missing-picture test serves 404s on purpose; every other page must stay error-free.
        if (!imagesMissing)
            _guards.Add(PlaywrightErrorGuard.Watch(page));
        return (context, page);
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "popunkoutersoftware-e2eui-shots");
        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name), FullPage = false });
    }

    private static async Task<int> SidewaysOverflowAsync(IPage page) =>
        await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

    // ─── Showroom ───────────────────────────────────────────────────────────────

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Showroom_ShowsCapturesInlineFromGitHub_AndPagesThroughTheReel(string label, int width, int height, bool isMobile)
    {
        var (context, page) = await NewPageAsync(helperConnected: false, width: width, height: height, isMobile: isMobile);
        await page.GotoAsync($"{BaseUrl}/global/showroom", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var hero = page.Locator(".sr-hero img").First;
        await Assertions.Expect(hero).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await hero.GetAttributeAsync("src")).Should().StartWith("https://raw.githubusercontent.com/punkouter27/");
        (await hero.GetAttributeAsync("src")).Should().NotContain("/PoBox/PoBox/", "the path inside the repo drops the repo folder");
        await Assertions.Expect(page.GetByText("Only captures pushed to GitHub appear")).ToBeVisibleAsync();
        (await SidewaysOverflowAsync(page)).Should().BeLessThanOrEqualTo(1, $"the wall must not scroll sideways at {label}");
        await CaptureAsync(page, $"global-showroom-{label}.png");

        // The first card with a reel of 2+ pages through it with the keyboard.
        var card = page.Locator(".sr-card").Filter(new() { Has = page.Locator(".sr-thumbs") }).First;
        await card.Locator(".sr-hero").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(dialog.Locator(".sr-caption")).ToContainTextAsync("(1/");
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(dialog.Locator(".sr-caption")).ToContainTextAsync("(2/");
        await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(dialog.Locator(".sr-caption")).ToContainTextAsync("(1/");
        if (!isMobile)
            await CaptureAsync(page, "global-showroom-lightbox.png");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);

        // Filters narrow the wall.
        var all = await page.Locator(".sr-card").CountAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Blazor", Exact = true }).ClickAsync();
        var blazor = await page.Locator(".sr-card").CountAsync();
        blazor.Should().BeGreaterThan(0).And.BeLessThan(all);
        await Assertions.Expect(page.Locator(".sr-card .global-tag").First).ToHaveTextAsync("Blazor");
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task Showroom_APictureThatIsNotOnGitHub_ShowsAQuietPlaceholder()
    {
        var (context, page) = await NewPageAsync(helperConnected: false, imagesMissing: true);
        await page.GotoAsync($"{BaseUrl}/global/showroom", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var placeholder = page.Locator(".sr-hero .sr-missing").First;
        await Assertions.Expect(placeholder).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(placeholder).ToHaveTextAsync("picture not on GitHub");
        _imageRequests.Should().Contain(u => u.StartsWith("https://media.githubusercontent.com/media/", StringComparison.Ordinal),
            "a raw 404 retries the Git LFS address before giving up");
        await context.CloseAsync();
    }

    // ─── Ship Board ─────────────────────────────────────────────────────────────

    [LiveAppFact]
    public async Task ShipBoard_EditsAreKeptLocally_SavedThroughTheHelper_AndPushedWithoutACrawl()
    {
        var (context, page) = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/global/readiness", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var app = page.Locator(".rb-app").First;
        await Assertions.Expect(app).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync();
        var repo = (await app.Locator(".rb-hd strong").InnerTextAsync()).Trim();

        // unknown → yes → no → unknown → yes, with the keyboard as well as the mouse.
        var item = app.Locator(".rb-ck").First;
        await item.ClickAsync();
        await Assertions.Expect(item).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex(": yes\\."));
        await item.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(item).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex(": no\\."));
        await item.ClickAsync();
        await Assertions.Expect(item).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex(": not answered\\."));
        await item.ClickAsync();
        await Assertions.Expect(page.GetByText("unsaved changes")).ToBeVisibleAsync();

        // Local edits survive a reload, as on the old page.
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByText("unsaved changes")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        var cardAgain = page.Locator(".rb-app").Filter(new() { HasText = repo }).First;
        await Assertions.Expect(cardAgain.Locator(".rb-ck").First).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex(": yes\\."));

        await page.GetByRole(AriaRole.Button, new() { Name = "Push to the live site" }).IsDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save to config" }).ClickAsync();
        await Assertions.Expect(page.GetByText("saved on the PC")).ToBeVisibleAsync();
        var save = _actions.Should().ContainSingle().Subject;
        save.GetProperty("action").GetString().Should().Be("save_config");
        save.GetProperty("arg").GetString().Should().Be("readiness");
        var entry = save.GetProperty("data").GetProperty("apps").GetProperty(repo);
        entry.GetProperty("checklist").EnumerateObject().Should().Contain(p => p.Value.ValueKind == JsonValueKind.True);
        save.GetRawText().Should().NotContain("null", "unanswered items are omitted, never written as null");

        await page.GetByRole(AriaRole.Button, new() { Name = "Push to the live site" }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        var push = _actions[^1];
        push.GetProperty("action").GetString().Should().Be("refresh");
        push.GetProperty("push").GetBoolean().Should().BeTrue();
        push.GetProperty("noCrawl").GetBoolean().Should().BeTrue();
        await CaptureAsync(page, "global-readiness-desktop.png");
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task ShipBoard_StageSelect_MovesTheCard_AndClosestToShippingIsListed()
    {
        var (context, page) = await NewPageAsync(helperConnected: false);
        await page.GotoAsync($"{BaseUrl}/global/readiness", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var app = page.Locator(".rb-app").First;
        await Assertions.Expect(app).ToBeVisibleAsync(new() { Timeout = 40_000 });
        var repo = (await app.Locator(".rb-hd strong").InnerTextAsync()).Trim();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save to config" })).ToHaveCountAsync(0);

        await app.Locator("select").SelectOptionAsync("shipped");
        var shipped = page.GetByRole(AriaRole.Region, new() { Name = "Shipped" });
        await Assertions.Expect(shipped.Locator(".rb-app").Filter(new() { HasText = repo })).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".rb-closest li").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".rb-closest")).Not.ToContainTextAsync(repo + " ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard my edits" }).ClickAsync();
        await Assertions.Expect(page.GetByText("unsaved changes")).ToHaveCountAsync(0);
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task ShipBoard_OnAPhone_TheBoardScrollsInsideItself_NotThePage()
    {
        var (context, page) = await NewPageAsync(helperConnected: false, width: 390, height: 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}/global/readiness", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.Locator(".rb-app").First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await SidewaysOverflowAsync(page)).Should().BeLessThanOrEqualTo(1);
        var inner = await page.Locator(".rb-board").EvaluateAsync<int>("b => b.scrollWidth - b.clientWidth");
        inner.Should().BeGreaterThan(0, "five columns are wider than a phone, so the board scrolls inside itself");
        await CaptureAsync(page, "global-readiness-mobile.png");
        await context.CloseAsync();
    }

    // ─── Idea Incubator ─────────────────────────────────────────────────────────

    [LiveAppTheory]
    [InlineData("desktop", 1440, 1000, false)]
    [InlineData("mobile", 390, 844, true)]
    public async Task Incubator_RanksStartingPoints_AndRemembersTheAnswers(string label, int width, int height, bool isMobile)
    {
        var (context, page) = await NewPageAsync(helperConnected: false, width: width, height: height, isMobile: isMobile);
        await page.GotoAsync($"{BaseUrl}/global/incubator", new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Default answers (physics, humanoid, 1v1, desktop, fast) put an RL humanoid gym first.
        var top = page.Locator(".inc-rec.is-top");
        await Assertions.Expect(top).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(page.Locator(".inc-rec")).ToHaveCountAsync(3);
        await Assertions.Expect(top).ToContainTextAsync("Day-1 brains");
        await Assertions.Expect(top).ToContainTextAsync("Same physics family");
        await Assertions.Expect(top.Locator("a[href^='/global/lessons#repo=']")).ToHaveCountAsync(1);
        await Assertions.Expect(top.Locator(".inc-cmd code")).ToContainTextAsync("PoNewGame");
        (await SidewaysOverflowAsync(page)).Should().BeLessThanOrEqualTo(1, $"the incubator must not scroll sideways at {label}");
        await CaptureAsync(page, $"global-incubator-{label}.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Web app or tool (no RL)" }).ClickAsync();
        await Assertions.Expect(top.Locator(".global-tag")).ToHaveTextAsync("Blazor");
        await Assertions.Expect(top.Locator(".inc-cmd code")).ToContainTextAsync("PoNewApp");
        await Assertions.Expect(top).Not.ToContainTextAsync("Day-1 brains");

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Web app or tool (no RL)" }))
            .ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = 40_000 });
        await context.CloseAsync();
    }
}
