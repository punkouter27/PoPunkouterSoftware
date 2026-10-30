using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// E2EUI — C# Playwright tests driving a real browser against the running app.
/// Point at a live instance via BASE_URL (default http://localhost:8000); start it with
/// F5 or `dotnet run`. These are run locally / on demand — never in CI/CD.
/// Every test runs at BOTH mobile-portrait and desktop-landscape form factors so the two
/// renderings cannot drift apart unnoticed.
/// First run requires browsers: pwsh bin/Debug/net10.0/playwright.ps1 install
/// </summary>
public class PortfolioUiTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000";

    /// <summary>label, width, height, isMobile — the two supported form factors.</summary>
    public static TheoryData<string, int, int, bool> Viewports => new()
    {
        { "mobile", 390, 844, true },
        { "desktop", 1440, 1000, false },
    };

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        // Headless bundled Chromium by default; HEADED=1 shows the browser and
        // BROWSER_CHANNEL=chrome drives the installed Google Chrome instead.
        _browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL"),
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    // HasTouch tracks IsMobile because the viewport-fit paging on /azure is gated on
    // `(hover: none)`, which Chromium only reports under touch emulation. Without it the
    // "mobile" page here would quietly get the desktop layout and every mobile assertion
    // below would be testing something the real device never sees.
    private async Task<IPage> NewPageAsync(int width, int height, bool isMobile) =>
        await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
        });

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Portfolio_Home_LoadsAndShowsBrand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        (await page.TitleAsync()).Should().Contain("PoPunkouterSoftware");
        await Assertions.Expect(page.GetByText("PoPunkouterSoftware").First).ToBeVisibleAsync();
        // The header is the brand and nothing else (links hidden 2026-10-20, removed 2026-10-30).
        await Assertions.Expect(page.Locator(".app-topbar a")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".app-portfolio-card").First).ToBeVisibleAsync();
        await CaptureAsync(page, $"01-home-{label}.png");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_DefaultIsCompact_AndAdvancedDetailsAreOnDemand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        // "Month-end forecast" is the first always-present glance card heading; the hero h1
        // is dynamic ("Everything looks good" / "N items need attention") so it is not a
        // stable anchor. "All resources" only exists once the advanced section is expanded.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Month-end forecast" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".azure-glance-grid > article")).ToHaveCountAsync(3);
        await CaptureAsync(page, $"02-azure-status-{label}.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToBeVisibleAsync();
        await CaptureAsync(page, $"03-azure-advanced-{label}.png");
    }

    /// <summary>
    /// Both routes must reflow without horizontal overflow AND without logging an error.
    ///
    /// <para>The console half was added with the GPU/audio layers. Every one of them is
    /// designed to degrade rather than fail — a missing WebGL2, a shader that will not compile, a
    /// blocked AudioContext — and each degradation path is a `console.warn`. An
    /// <c>error</c> means a path nobody designed was taken, and because the layers are
    /// decorative, nothing else on the page would show it.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_ReflowWithoutOverflowOrConsoleErrors(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var errors = new List<string>();
        page.Console += (_, msg) => { if (msg.Type == "error") errors.Add(msg.Text); };
        page.PageError += (_, err) => errors.Add(err);

        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"home must not overflow horizontally at {label}");
        await CaptureAsync(page, $"04-home-reflow-{label}.png");

        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"/azure must not overflow horizontally at {label}");
        await CaptureAsync(page, $"05-azure-reflow-{label}.png");

        errors.Should().BeEmpty($"no route may log a console error at {label}");
    }

    /// <summary>
    /// The app must be silent until a visitor asks for sound, and the backdrop ladder must
    /// resolve to a state it knows about rather than throwing.
    ///
    /// <para>Silence-by-default is an accessibility floor, not a preference: an AudioContext
    /// constructed during page load is both an unrequested hardware claim and — under every
    /// current autoplay policy — a suspended context that silently swallows whatever is
    /// played through it. So the assertion is specifically that no context has been
    /// CONSTRUCTED, which is stronger and more observable than "nothing was audible".</para>
    ///
    /// <para>The tier is deliberately not asserted. Headless Chromium usually resolves to
    /// SwiftShader, which <c>failIfMajorPerformanceCaveat</c> correctly refuses, so
    /// <c>no-gpu</c> is a legitimate outcome here — the invariant is that the ladder reached
    /// one of its DEFINED states, never that it reached the top one.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Backdrop_ResolvesATier_AndStaysSilentUntilAsked(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        // "webgpu" is deliberately absent: that rung was removed on 2026-09-05, so a browser
        // reporting it would mean the tier vocabulary and the ladder had drifted apart.
        var known = new[] { "webgl2", "webgl2-field", "webgl1", "no-gpu", "reduced-motion", "context-lost" };
        var tier = await page.EvaluateAsync<string?>(
            "() => document.getElementById('app-gpu-backdrop')?.dataset.gpu ?? null");
        tier.Should().BeOneOf(known, $"the backdrop ladder must land on a defined state at {label}");

        var audio = await page.EvaluateAsync<bool[]>(
            "() => [window.audioKit.enabled, window.audioKit.stats().contextCreated]");
        audio[0].Should().BeFalse("sound must default to off");
        audio[1].Should().BeFalse("no AudioContext may be constructed before the visitor asks for one");

        // /settings is the only home of the sound preference (the palette's toggle went on
        // 2026-10-19). The click is itself the user gesture the autoplay policy requires, so
        // one click must both flip the preference and bring a live context into existence.
        // Turned back off afterwards: the choice persists in localStorage.
        await page.GotoAsync($"{BaseUrl}/settings", new() { WaitUntil = WaitUntilState.NetworkIdle });
        var soundOn = page.GetByRole(AriaRole.Group, new() { Name = "Sound" }).GetByRole(AriaRole.Button, new() { Name = "On" });
        await Assertions.Expect(soundOn).ToBeEnabledAsync();
        await soundOn.ClickAsync();
        (await page.EvaluateAsync<bool[]>("() => [window.audioKit.enabled, window.audioKit.stats().contextCreated]"))
            .Should().Equal([true, true], $"turning sound on must enable it and construct the context at {label}");
        await page.GetByRole(AriaRole.Group, new() { Name = "Sound" }).GetByRole(AriaRole.Button, new() { Name = "Off" }).ClickAsync();
    }

    /// <summary>
    /// Reduced motion must STOP every frame loop, not merely hide its output.
    ///
    /// <para>Each animated layer used to own a private rAF loop and a private pause; the
    /// governor in js/motion-kit.js now owns all of them, and its `running` flag is the
    /// observable form of the guarantee. A layer that faded to `opacity: 0` while still
    /// being scheduled would pass any screenshot check and still burn a phone's battery.</para>
    /// </summary>
    [Fact]
    public async Task ReducedMotion_StopsEveryFrameLoop()
    {
        await using var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ReducedMotion = ReducedMotion.Reduce,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        // Two bools rather than the whole stats object: EvaluateAsync of a nested structure
        // adds a deserialisation step that can only obscure what is being asserted.
        var flags = await page.EvaluateAsync<bool[]>(
            "() => [window.motionKit.stats().reduced, window.motionKit.stats().running]");
        flags[0].Should().BeTrue("the governor must see the reduced-motion query");
        flags[1].Should().BeFalse("no rAF loop may be scheduled under reduced motion");

        // And the backdrop must not have built GPU resources it can never draw with.
        (await page.EvaluateAsync<string?>("() => document.getElementById('app-gpu-backdrop')?.dataset.gpu ?? null"))
            .Should().Be("reduced-motion");
    }

    // ─── Regressions inherited from the 2026 UI pass ──────────────────────────
    // These four survived the temporary UiRedesignVerification harness because each
    // guards a defect that can recur from an ordinary CSS or markup edit. The rest of
    // that harness asserted one-time migration facts (cards no longer carry
    // backdrop-filter, the rz-sidebar column is collapsed, the topbar's pixel-level
    // spatial contract) and was retired with it.

    /// <summary>
    /// The glance panels must actually draw their metrics.
    ///
    /// <para>This used to assert two <c>RadzenChart</c> instances had non-zero height and had
    /// emitted SVG geometry — the failure it guarded was a scoped-CSS rule ceasing to match,
    /// after which Radzen measured a zero-height box and drew an empty strip that still looked
    /// "present" in a screenshot. Both charts were replaced by <c>MetricBars</c> (see that
    /// component's header for why), so the same defect now takes a different shape: a bar
    /// whose fill width resolves to zero, or a panel that renders no rows at all. The
    /// invariant is unchanged — a panel that reserves space must put marks in it.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_GlancePanels_DrawTheirMetrics(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-glance-grid", new() { Timeout = 40_000 });
        await page.WaitForSelectorAsync(".azure-glance-grid .metric-bar", new() { Timeout = 40_000 });

        // Every row must have a laid-out track and a fill with real width inside it. A zero
        // here is the bar-shaped form of the collapsed chart this test replaced.
        var fills = await page.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.azure-glance-grid .metric-bar__fill')]" +
            ".map(el => el.getBoundingClientRect().width)");
        fills.Should().NotBeEmpty($"no metric bars rendered at {label}");
        fills.Should().OnlyContain(w => w > 0, $"a metric bar drew no fill at {label}");

        // The leader is scaled to 100%, so the widest bar must fill most of its track.
        var ratio = await page.EvaluateAsync<double>(@"() => {
            const fill = [...document.querySelectorAll('.azure-glance-grid .metric-bar__fill')]
                .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)[0];
            return fill.getBoundingClientRect().width / fill.parentElement.getBoundingClientRect().width;
        }");
        ratio.Should().BeGreaterThan(0.9, $"the top-ranked bar must fill its track at {label}");

        // The forecast ring is the third panel and is still inline SVG.
        (await page.Locator(".azure-forecast__ring svg").CountAsync())
            .Should().Be(1, $"the forecast ring must render at {label}");
    }

    /// <summary>
    /// The catalogue's first-screen contract at mobile portrait: heading, freshness row and a
    /// WHOLE first card, inside 844px minus the chrome.
    ///
    /// <para>`/` deliberately keeps ordinary scrolling — a 24-item catalogue is a list, and
    /// paging a list fights the reader — so the guarantee it makes instead is that the answer
    /// is complete above the fold. Nothing enforced that before, and it is exactly the sort of
    /// thing an innocent type-scale or padding change breaks silently: the existing
    /// horizontal-overflow test passes just as happily with the first card pushed off-screen.
    /// The assertion is that the card's BOTTOM edge is above the fold, not merely its top —
    /// half a card is not a card.</para>
    /// </summary>
    [Fact]
    public async Task Home_FirstScreen_ShowsAWholeCard()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        var probe = await page.EvaluateAsync<double[]>(@"() => {
            const card = document.querySelector('.app-portfolio-card');
            const summary = document.querySelector('.portfolio-summary');
            const h1 = document.querySelector('.portfolio-header h1');
            return [
                h1.getBoundingClientRect().bottom,
                summary.getBoundingClientRect().bottom,
                card.getBoundingClientRect().bottom,
                window.innerHeight
            ];
        }");

        var fold = probe[3];
        probe[0].Should().BeLessThan(fold, "the page title must be above the fold");
        probe[1].Should().BeLessThan(fold, "the freshness row must be above the fold");
        probe[2].Should().BeLessThanOrEqualTo(fold,
            "the whole first card must fit the first screen at 390x844 — half a card is not a card");
    }

    /// <summary>
    /// /azure on a phone: an ordinary scrolling page (the one-pane-per-screen pager went on
    /// 2026-10-19), whose uptime grid opens on TODAY.
    ///
    /// <para>The grid is 30 columns in a ~320px scroller. It used to open at its left edge —
    /// the oldest day — so on an estate with a few days of history the card showed nothing but
    /// empty boxes, with every cell that had data off-screen to the right. The last day column
    /// of the first row must be inside the scroller's visible box on first paint.</para>
    ///
    /// <para>Element bounds, not scrollWidth, for the overflow half, for the reason
    /// <see cref="Users_RostersRender_AndNothingOverflowsTheBody"/> gives.</para>
    /// </summary>
    [Fact]
    public async Task Azure_MobilePortrait_UptimeOpensOnToday_AndNothingOverflows()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-uptime__scroll", new() { Timeout = 40_000 });

        var todayVisible = await page.EvaluateAsync<bool>(@"() => {
            const scroller = document.querySelector('.azure-uptime__scroll').getBoundingClientRect();
            const cells = document.querySelector('.azure-uptime__grid').querySelectorAll('.azure-uptime__cell');
            const days = document.querySelectorAll('.azure-uptime__label').length;
            const today = cells[cells.length / days - 1].getBoundingClientRect();
            return today.left >= scroller.left - 1 && today.right <= scroller.right + 1;
        }");
        todayVisible.Should().BeTrue("a phone must open the uptime grid on the newest day, not the oldest");

        var overhang = await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('main.azure-ops-page *')]
                .filter(e => !e.closest('.azure-uptime__scroll, dialog'))
                .filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1)
                .map(e => e.tagName + '.' + e.className + ' @' + Math.round(e.getBoundingClientRect().right) + 'px')");
        overhang.Should().BeEmpty("nothing outside the uptime scroller may run off the right of a 390px screen");
    }

    /// <summary>
    /// The glance grid must actually reflow, not merely avoid overflowing.
    ///
    /// <para>Its responsive rules were written without <c>::deep</c> while the base rule had
    /// it, which compiles to LOWER specificity for the override — media queries add none, so
    /// the 3-column base won at every width and a phone rendered three 109px-wide charts side
    /// by side. Nothing caught it: <c>CoreRoutes_ReflowWithoutHorizontalOverflow</c> passes
    /// because <c>minmax(0,1fr)</c> squeezes rather than overflows, and
    /// <c>Azure_GlanceCharts_HaveHeightAndDrawGeometry</c> only checks height.</para>
    ///
    /// <para>Asserting the tiles are stacked (equal left edge) rather than reading
    /// <c>grid-template-columns</c> keeps this about the visible result.</para>
    /// </summary>
    [Fact]
    public async Task Azure_GlanceCards_StackVertically_OnMobile()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-glance-grid > article", new() { Timeout = 40_000 });

        var boxes = await page.EvaluateAsync<double[][]>(
            "() => [...document.querySelectorAll('.azure-glance-grid > article')].map(el => " +
            "{ const r = el.getBoundingClientRect(); return [r.left, r.width]; })");

        boxes.Should().HaveCount(3);
        boxes.Select(b => b[0]).Distinct().Should().ContainSingle(
            "the three glance cards must share a left edge — i.e. be stacked, not side by side");
        boxes.Should().OnlyContain(b => b[1] > 250,
            "a full-width card at 390px must be far wider than a 1/3 column");
    }

    /// <summary>
    /// The resource explorer is ONE tree at every viewport.
    ///
    /// <para>It used to be two — a card list and a RadzenDataGrid — first both rendered with
    /// CSS hiding the wrong one (every row built twice), then behind a matchMedia bridge
    /// picking between them. The grid is gone: it carried sorting, per-column filter menus,
    /// column resize and virtualization over a list that measures 48 rows, and its filter
    /// menus put buttons named "*A*Contains" into the accessibility tree. The explorer is now a single
    /// table that folds its metric columns on a phone, so there is no branch left. This test now guards against a second tree being
    /// reintroduced rather than against both rendering at once.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_ResourceExplorer_RendersExactlyOneTree(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await page.WaitForSelectorAsync(".azure-resource-table", new() { Timeout = 40_000 });

        (await page.Locator(".azure-resource-table").CountAsync())
            .Should().Be(1, $"exactly one explorer tree must be in the DOM at {label}");
        (await page.Locator(".azure-resource-grid, .rz-data-grid, .rz-datatable").CountAsync())
            .Should().Be(0, "the data grid was removed; a returning one is a regression");
        (await page.Locator(".azure-resource-row").CountAsync())
            .Should().BeGreaterThan(0, "the explorer must actually list resources");
    }

    /// <summary>
    /// Radzen bakes component colours per theme sheet, so a control styled for one scheme
    /// can render invisible in the other (ButtonStyle.Light was white-on-white in light
    /// mode). Both schemes must clear the WCAG AA 4.5:1 floor.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Azure_AdvancedToggle_IsLegibleInBothColorSchemes(string scheme)
    {
        await using var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-advanced-toggle .rz-button", new() { Timeout = 40_000 });

        // Walk up for the first non-transparent ancestor: the button's own background is
        // transparent in the outlined variant, so comparing against it would always pass.
        var probe = await page.EvaluateAsync<string[]>(@"() => {
            const btn = document.querySelector('.azure-advanced-toggle .rz-button');
            const cs = getComputedStyle(btn);
            let bg = cs.backgroundColor, el = btn;
            while ((bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent') && el.parentElement) {
                el = el.parentElement; bg = getComputedStyle(el).backgroundColor;
            }
            return [cs.color, bg, getComputedStyle(document.documentElement).getPropertyValue('--rz-primary').trim()];
        }");

        ContrastRatio(probe[0], probe[1]).Should()
            .BeGreaterThanOrEqualTo(4.5, $"'Advanced diagnostics' is illegible in {scheme} mode");

        // --rz-primary must resolve to the app accent, not Radzen's stock #598087 teal.
        probe[2].Should().NotBeNullOrWhiteSpace("--rz-primary is unset");
        probe[2].Should().NotContain("598087", "the accent silently fell back to Radzen stock");
    }

    /// <summary>WCAG relative-luminance contrast ratio between two computed CSS colours.</summary>
    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string css)
        {
            var parts = System.Text.RegularExpressions.Regex.Matches(css, @"\d+(\.\d+)?");
            double Channel(int i)
            {
                var c = double.Parse(parts[i].Value) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
        }

        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// axe-core's WCAG 2.1 A/AA rules over all four pages, failing on serious or critical
    /// violations only.
    ///
    /// <para>Every accessibility defect this repo has fixed was found by hand — Radzen icon
    /// names leaking into button labels, a literal <c>aria-disabled</c> expression, badges at
    /// 1.64:1 contrast — and each needed its own bespoke test afterwards. axe catches that whole
    /// class in one pass, so the next one is found by a red test rather than a person. Minor
    /// and moderate findings are left out of the gate on purpose: axe reports them liberally
    /// and a gate that is always red is a gate nobody reads.</para>
    ///
    /// <para>It also holds the two landmark checks that used to be a test of their own, on the
    /// same four page loads: exactly one <c>&lt;main&gt;</c>, and a skip link that resolves.
    /// <c>/azure</c> once had no <c>&lt;main&gt;</c> — its root was a bare div — so "Skip to
    /// content" was a link to nothing there while the identical link on "/" worked.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_HaveOneMainLandmark_AWorkingSkipLink_AndNoSeriousAxeViolations(
        string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var found = new List<string>();

        foreach (var (route, ready) in new[] { ("/", ".app-portfolio-card"), ("/azure", ".azure-uptime"), ("/users", "main h1"), ("/settings", ".settings-row button:not([disabled])") })
        {
            await page.GotoAsync($"{BaseUrl}{route}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync(ready, new() { Timeout = 40_000 });
            // appRepairRadzenA11y hides each Radzen icon ligature from the accessibility tree via
            // a MutationObserver; auditing before it has run reports "syncSync"-style names
            // (label-content-name-mismatch) that no visitor ever meets.
            await page.WaitForFunctionAsync("() => !document.querySelector('.rzi:not([aria-hidden])')");
            // `.app-page` fades in from opacity 0. /settings is "ready" the instant its buttons
            // enable, so under load axe measured its subtitle mid-fade and reported a contrast
            // failure no visitor sees. Let every finite time-based animation land first.
            await page.EvaluateAsync(@"() => Promise.all(document.getAnimations()
                .filter(a => a.timeline === document.timeline && a.effect?.getTiming().iterations !== Infinity)
                .map(a => a.finished.catch(() => {})))");

            (await page.Locator("main").CountAsync()).Should().Be(1, $"{route} needs exactly one main landmark at {label}");
            (await page.EvaluateAsync<bool>(@"() => {
                const link = document.querySelector('.app-skip-link');
                return !!(link && document.querySelector(link.getAttribute('href')));
            }")).Should().BeTrue($"the skip link must resolve on {route} at {label}");

            var result = await page.RunAxe(new Deque.AxeCore.Commons.AxeRunOptions
            {
                RunOnly = new Deque.AxeCore.Commons.RunOnlyOptions
                {
                    Type = "tag",
                    Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
                },
            });
            found.AddRange(result.Violations
                .Where(v => v.Impact is "serious" or "critical")
                .Select(v => $"{route} [{v.Impact}] {v.Id}: {v.Nodes.Length} node(s), e.g. {v.Nodes[0].Html[..Math.Min(200, v.Nodes[0].Html.Length)]}"));
        }

        found.Should().BeEmpty($"axe found serious/critical WCAG violations at {label}");
    }

    /// <summary>
    /// Every icon must render as a GLYPH, not as its own name.
    ///
    /// <para>Material Symbols draws through ligatures — the element's text is the icon's name —
    /// so the font subset in <c>wwwroot/fonts</c> (see SCRIPTS/Build-IconFontSubset.py, which
    /// replaced a 1 MB full font with 16 KB) fails in one specific way: a name missing from the
    /// subset does not go blank, it renders the literal word "refresh" inside a button. This
    /// measures rendered width against font size, which is the only signal that separates the
    /// two, and it opens every disclosure first so the icons behind them are covered too.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_EveryIcon_RendersAsAGlyphNotItsName(
        string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await page.WaitForSelectorAsync(".azure-resource-table", new() { Timeout = 40_000 });
        await page.EvaluateAsync("() => document.querySelectorAll('details').forEach(d => { d.open = true; })");
        await page.WaitForTimeoutAsync(800);

        var words = await page.EvaluateAsync<string[]>(@"() => {
            const bad = [];
            for (const el of document.querySelectorAll('*')) {
                const cs = getComputedStyle(el);
                if (!/Material Symbols/i.test(cs.fontFamily)) continue;
                const text = (el.textContent || '').trim();
                if (!text || !/^[a-z0-9_]+$/.test(text)) continue;
                const width = el.getBoundingClientRect().width;
                if (width > parseFloat(cs.fontSize) * 2.2) bad.push(text + ' @' + Math.round(width) + 'px');
            }
            return bad;
        }");
        words.Should().BeEmpty($"these icon names have no glyph in the font subset at {label}");

        var fullFont = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(r => /MaterialSymbolsOutlined/i.test(r.name)).length");
        fullFont.Should().Be(0, "the 1 MB Radzen icon font must never be fetched");
    }

    /// <summary>
    /// The third route. Covers the two things about /users that only a real browser can show:
    /// that the WASM island actually paints the roster (the page has no server-rendered
    /// content at all), and that nothing on it scrolls the page body sideways at 390px — the
    /// contract the whole app holds to, and the one a table of five text columns is most
    /// likely to break. The roster and the raw feed are allowed their own horizontal
    /// scrollers; the BODY is not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Users_RostersRender_AndNothingOverflowsTheBody(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync($"{BaseUrl}/users", new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        await Assertions.Expect(page.Locator("main.signins-page")).ToBeVisibleAsync();

        // Either branch is a correct render — a deployment with no Monitoring Reader role
        // shows the reason instead of the roster — but a page stuck on the skeleton is not.
        var unavailable = await page.Locator(".signins-unavailable").CountAsync();
        if (unavailable == 0)
        {
            await Assertions.Expect(page.Locator(".signins-stats")).ToBeVisibleAsync();
            // The masking contract, asserted over the rendered TEXT rather than the API
            // response — this is what a visitor can actually read — and over every token that
            // looks like an address rather than over one CSS class. The roster omits the email
            // line when the provider sent no real name (both fields mask to the same string),
            // so a class-scoped assertion would silently stop covering those rows.
            var addresses = await page.EvaluateAsync<string[]>(
                @"() => (document.querySelector('main.signins-page').innerText.match(/\S+@\S+/g) || [])");
            addresses.Should().NotBeEmpty("the roster shows at least one address");
            addresses.Should().OnlyContain(a => a.Contains('*'), "every address on screen is masked");

            // Open both disclosures: a RadzenChart that never paints reports no error and
            // leaves an empty box, so the SVG has to be asserted rather than the container.
            await page.EvaluateAsync("() => document.querySelectorAll('details.app-disclosure').forEach(d => d.open = true)");
            await Assertions.Expect(page.Locator(".app-disclosure .rz-chart svg").First).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".app-disclosure .app-table").First).ToBeVisibleAsync();
        }

        // Measured with everything expanded — a collapsed <details> cannot overflow, so
        // checking this before opening them would prove nothing about the widest state.
        //
        // This measures ELEMENT BOUNDS, not document.scrollWidth, and the difference is the
        // whole point. An over-wide grid track gets clipped rather than scrolled, so
        // scrollWidth stayed exactly equal to clientWidth while the header, the KPI tiles and
        // every card border ran 51px off the right of a 390px screen. CLAUDE.md records the
        // same trap on /azure. Elements inside a deliberate overflow-x container are excluded:
        // those are allowed to run wide, because the visitor can scroll them.
        var overhang = await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('main.signins-page *')]
                .filter(e => !e.closest('.app-table-scroll, .app-disclosure__body'))
                .filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1)
                .map(e => e.tagName + '.' + e.className + ' @' + Math.round(e.getBoundingClientRect().right) + 'px')");
        overhang.Should().BeEmpty($"nothing outside a scroll container may run off the right at {label}");

        // …and the roster's scroller is an escape hatch, not the layout: at 390px the sign-in
        // count column sat past its edge, cut mid-number, with nothing hinting it scrolled.
        if (unavailable == 0)
        {
            var rosterOverflow = await page.EvaluateAsync<int>(
                "() => { const s = document.querySelector('.app-table-scroll'); return s.scrollWidth - s.clientWidth; }");
            rosterOverflow.Should().BeLessThanOrEqualTo(1, $"the roster must fit its card without a sideways scroll at {label}");
        }

        await CaptureAsync(page, $"09-users-{label}.png");
    }

    private static async Task CaptureAsync(IPage page, string filename)
    {
        if (Environment.GetEnvironmentVariable("CAPTURE_SCREENSHOTS") != "1")
            return;

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ux-audit"));
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true });
    }
}

