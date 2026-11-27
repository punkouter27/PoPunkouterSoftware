using System.Text.Json;
using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// The GPU queue planner and the brain vault in a real browser. The local helper is a Playwright
/// route (as in GlobalUiTests), so nothing runs on the machine: these tests prove what the pages
/// send — job ids, hours and brain paths, never a command — and that the planner and stars work
/// without the helper too.
/// </summary>
public class GlobalStationUiTestsB : IAsyncLifetime
{
    private const string HelperToken = "browser-test-token-0123456789abcdef";
    private static readonly string BaseUrl = LiveApp.BaseUrl;
    private static readonly string[] IdAndHours = ["hours", "id"];

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

    private async Task<(IBrowserContext Context, IPage Page)> NewPageAsync(bool connected, int width = 1440, int height = 1000, bool isMobile = false)
    {
        var context = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
            AcceptDownloads = true,
        });
        await context.RouteAsync($"{BaseUrl}/**", route => route.ContinueAsync(new()
        {
            Headers = new Dictionary<string, string>(route.Request.Headers) { ["X-Fake-User"] = "browser-test" },
        }));
        if (connected)
            await context.AddInitScriptAsync($"localStorage.setItem('po.localHelper.token', '{HelperToken}')");

        await context.RouteAsync("http://127.0.0.1:5050/**", async route =>
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

        var page = await context.NewPageAsync();
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

    private static void SendsNoCommands(JsonElement sent)
    {
        var raw = sent.GetRawText();
        raw.Should().NotContain("\"command\"", "the helper takes every command from its own catalog");
        raw.Should().NotContain("\"cwd\"");
    }

    // ─── GPU queue ─────────────────────────────────────────────────────────────

    [LiveAppFact]
    public async Task Queue_Planner_WorksWithoutTheHelper_PersistsAndDownloads()
    {
        var (context, page) = await NewPageAsync(connected: false);
        await page.GotoAsync($"{BaseUrl}/global/queue", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "1 · What needs training most" })).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await page.Locator(".queue-urg").CountAsync()).Should().BeInRange(1, 12);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Past queue runs" })).ToBeVisibleAsync();

        await page.Locator(".queue-urg-jobs button").First.ClickAsync();
        await page.Locator(".queue-catalog button.queue-add").Nth(1).ClickAsync();
        var items = page.Locator(".queue-item");
        await Assertions.Expect(items).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".queue-eta")).ToContainTextAsync("ends around");

        var secondName = await items.Nth(1).Locator(".queue-item-name strong").InnerTextAsync();
        await items.Nth(1).GetByRole(AriaRole.Button, new() { NameRegex = new("^Move .* up$") }).ClickAsync();
        (await items.Nth(0).Locator(".queue-item-name strong").InnerTextAsync()).Should().EndWith(secondName[3..]);

        await items.Nth(0).Locator("input[type=number]").FillAsync("2.5");
        await items.Nth(0).Locator("input[type=number]").PressAsync("Tab");

        var download = await page.RunAndWaitForDownloadAsync(() =>
            page.GetByRole(AriaRole.Button, new() { Name = "Download queue JSON" }).ClickAsync());
        download.SuggestedFilename.Should().Be("tonight.json");
        var file = Path.Combine(Path.GetTempPath(), $"po-queue-{Guid.NewGuid():N}.json");
        await download.SaveAsAsync(file);
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file)))
        {
            var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray().ToList();
            jobs.Should().HaveCount(2);
            jobs[0].GetProperty("hours").GetDouble().Should().Be(2.5);
            jobs[0].GetProperty("command").GetString().Should().NotBeNullOrEmpty("the downloaded file is what run_gpu_queue.ps1 reads");
            doc.RootElement.GetProperty("cool_temp").GetInt32().Should().Be(70);
        }

        File.Delete(file);

        // No helper: nothing to start, and nothing was sent.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start on this PC…" })).ToHaveCountAsync(0);
        _actions.Should().BeEmpty();

        // The plan survives a reload (localStorage), and ✕ removes an item.
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator(".queue-item")).ToHaveCountAsync(2, new() { Timeout = 40_000 });
        await page.Locator(".queue-item").Nth(1).GetByRole(AriaRole.Button, new() { NameRegex = new("^Remove ") }).ClickAsync();
        await Assertions.Expect(page.Locator(".queue-item")).ToHaveCountAsync(1);
        await CaptureAsync(page, "global-queue-planner-desktop.png");
        (await SidewaysOverflowAsync(page)).Should().BeLessThanOrEqualTo(1);
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task Queue_Connected_SaveAndLaunchSendOnlyIdsAndHours()
    {
        var (context, page) = await NewPageAsync(connected: true);
        await page.GotoAsync($"{BaseUrl}/global/queue", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await page.Locator(".queue-catalog button.queue-add").First.ClickAsync();
        await page.Locator(".queue-catalog button.queue-add").Nth(2).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Save on this PC" }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        var save = _actions.Should().ContainSingle().Subject;
        save.GetProperty("action").GetString().Should().Be("save_queue");
        save.GetProperty("jobs").GetArrayLength().Should().Be(2);
        SendsNoCommands(save);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start on this PC…" }).ClickAsync();
        _actions.Should().HaveCount(1, "arming the confirmation sends nothing");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Yes, start 2 job") }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");

        var launch = _actions[^1];
        launch.GetProperty("action").GetString().Should().Be("launch_queue");
        launch.TryGetProperty("dry", out var dry).Should().BeTrue();
        dry.GetBoolean().Should().BeFalse();
        launch.GetProperty("name").GetString().Should().Be("tonight");
        launch.GetProperty("coolTemp").GetInt32().Should().Be(70);
        launch.GetProperty("jobs").EnumerateArray().Should().OnlyContain(j =>
            j.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(IdAndHours));
        SendsNoCommands(launch);
        await context.CloseAsync();
    }

    [LiveAppTheory]
    [InlineData("/global/queue")]
    [InlineData("/global/vault")]
    public async Task Phone_NoSidewaysScroll(string path)
    {
        var (context, page) = await NewPageAsync(connected: true, 390, 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        await Assertions.Expect(page.Locator(".global-kpi").First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        if (path.EndsWith("queue", StringComparison.Ordinal))
            await page.Locator(".queue-catalog button.queue-add").First.ClickAsync();
        (await SidewaysOverflowAsync(page)).Should().BeLessThanOrEqualTo(1, $"{path} must not scroll sideways on a phone");
        await CaptureAsync(page, $"global-{path.Split('/')[^1]}-mobile.png");
        await context.CloseAsync();
    }

    // ─── Brain vault ───────────────────────────────────────────────────────────

    [LiveAppFact]
    public async Task Vault_SocketCardsFilter_AndBodiesTabLists()
    {
        var (context, page) = await NewPageAsync(connected: false);
        await page.GotoAsync($"{BaseUrl}/global/vault", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var rows = page.Locator(".vault-table tbody tr");
        await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 40_000 });
        (await page.Locator(".global-kpi").CountAsync()).Should().Be(7);
        var before = await rows.CountAsync();

        var card = page.Locator(".vault-sockcard").First;
        await Assertions.Expect(card).ToBeVisibleAsync();
        var socket = (await card.Locator(".vault-sock").InnerTextAsync()).Trim();
        await card.ClickAsync();
        await Assertions.Expect(page.Locator("select[aria-label='Filter by socket']")).ToHaveValueAsync(socket);
        var sockets = await page.Locator(".vault-table tbody tr .vault-sock").AllInnerTextsAsync();
        sockets.Should().NotBeEmpty().And.OnlyContain(s => s.Trim() == socket);
        (await rows.CountAsync()).Should().BeLessThanOrEqualTo(before);
        await CaptureAsync(page, "global-vault-desktop.png");

        // Critics are hidden until "policies only" is cleared.
        await page.Locator("select[aria-label='Filter by socket']").SelectOptionAsync("");
        var policiesOnly = await rows.CountAsync();
        await page.GetByLabel("policies only (hide critics)").UncheckAsync();
        (await rows.CountAsync()).Should().BeGreaterThanOrEqualTo(policiesOnly);

        await page.GetByRole(AriaRole.Tab, new() { Name = "Bodies (MJCF)" }).ClickAsync();
        await Assertions.Expect(page.Locator("table caption:text('MuJoCo bodies in the fleet')")).ToHaveCountAsync(1);
        await page.GetByLabel("Search bodies").FillAsync("g1");
        await Assertions.Expect(page.Locator("tbody tr").First).ToBeVisibleAsync();
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task Vault_WithoutTheHelper_StarsStayInTheBrowser()
    {
        var (context, page) = await NewPageAsync(connected: false);
        await page.GotoAsync($"{BaseUrl}/global/vault", new() { WaitUntil = WaitUntilState.NetworkIdle });

        var star = page.Locator(".vault-table tbody tr").First.Locator(".vault-star");
        await Assertions.Expect(star).ToBeVisibleAsync(new() { Timeout = 40_000 });
        var label = await star.GetAttributeAsync("aria-label");
        await star.ClickAsync();
        await Assertions.Expect(page.Locator($".vault-star[aria-label='{label}']")).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy import" }).First).ToBeVisibleAsync();
        _actions.Should().BeEmpty();

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator($".vault-star[aria-label='{label}']")).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = 40_000 });
        await context.CloseAsync();
    }

    [LiveAppFact]
    public async Task Vault_Connected_StarSavesGoldAndImportSendsThePathOnly()
    {
        var (context, page) = await NewPageAsync(connected: true);
        await page.GotoAsync($"{BaseUrl}/global/vault", new() { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.GetByText("This PC is connected")).ToBeVisibleAsync(new() { Timeout = 40_000 });
        var firstRow = page.Locator(".vault-table tbody tr").First;
        var rel = await firstRow.Locator("td").Nth(1).Locator("[title]").First.GetAttributeAsync("title");
        rel.Should().NotBeNullOrEmpty();

        await firstRow.Locator(".vault-star").ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        var save = _actions.Should().ContainSingle().Subject;
        save.GetProperty("action").GetString().Should().Be("save_config");
        save.GetProperty("arg").GetString().Should().Be("vault_gold");
        save.GetProperty("data").GetProperty("gold").TryGetProperty(rel!, out _).Should().BeTrue();

        await page.GetByRole(AriaRole.Button, new() { Name = "Push to the live site" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Push to the live site" })).ToHaveCountAsync(0);
        var push = _actions[^1];
        push.GetProperty("action").GetString().Should().Be("refresh");
        push.GetProperty("push").GetBoolean().Should().BeTrue();
        push.GetProperty("noCrawl").GetBoolean().Should().BeTrue();

        await firstRow.GetByRole(AriaRole.Button, new() { Name = "Import…" }).ClickAsync();
        var target = page.Locator(".vault-import select");
        await target.SelectOptionAsync(new SelectOptionValue { Index = 1 });
        var targetRepo = await target.InputValueAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Check fit (copies nothing)" }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");

        var check = _actions[^1];
        check.GetProperty("action").GetString().Should().Be("vault_import");
        check.GetProperty("brain").GetString().Should().Be(rel);
        check.GetProperty("target").GetString().Should().Be(targetRepo);
        check.GetProperty("dry").GetBoolean().Should().BeTrue();
        SendsNoCommands(check);

        // A real copy takes a second, explicit click.
        var count = _actions.Count;
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy…" }).ClickAsync();
        _actions.Should().HaveCount(count);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Yes, copy it into {targetRepo}" }).ClickAsync();
        await Assertions.Expect(page.Locator(".global-output")).ToContainTextAsync("helper reply: done");
        _actions[^1].GetProperty("dry").GetBoolean().Should().BeFalse();
        await context.CloseAsync();
    }
}
