using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Regressions from the UI/UX audit. Each test pins a defect that shipped, so an ordinary
/// edit cannot quietly reintroduce it.
/// </summary>
[Collection("WebApp")]
public class HealthContractRegressionTests
{
    private readonly HttpClient _client;

    public HealthContractRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The hermetic fixture blanks the vault keys to disable Key Vault. The check read them
    /// with <c>?? default</c>, which never fires on "" (only on null), so it probed the empty
    /// string, threw, and dragged the whole endpoint to 503 — every /health assertion in the
    /// suite failed for that one reason. A blank key means "no vault", which is healthy.
    /// </summary>
    [Fact]
    public async Task Health_WithVaultDisabled_Returns200_AndReportsKeyVaultNotConfigured()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keyVault = doc.RootElement.GetProperty("checks").GetProperty("KeyVault");
        keyVault.GetProperty("status").GetString().Should().Be("healthy");
        keyVault.GetProperty("description").GetString().Should().Be("not-configured");
    }

    /// <summary>
    /// The masked config block is part of the /health contract (NET_RULES §3) and is asserted
    /// by the E2E-API tier too. It was dropped when the checks were rewritten.
    /// </summary>
    [Fact]
    public async Task Health_CarriesMaskedConfig_WithNoRawSecretValues()
    {
        var json = await _client.GetStringAsync("/health");
        using var doc = JsonDocument.Parse(json);
        var config = doc.RootElement.GetProperty("config");

        config.GetProperty("ASPNETCORE_ENVIRONMENT").GetString().Should().Be("Testing");
        foreach (var prop in config.EnumerateObject().Where(p => p.Name != "ASPNETCORE_ENVIRONMENT"))
        {
            var value = prop.Value.GetString();
            var isMasked = value is "(not set)" or "****" or "configured (redacted)"
                           || (value?.Contains('*') ?? false);
            isMasked.Should().BeTrue(because: $"config key '{prop.Name}' must be masked, got '{value}'");
        }
    }
}

[Collection("WebApp")]
public class OpsSummaryProjectionRegressionTests
{
    private readonly HttpClient _client;

    public OpsSummaryProjectionRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    private async Task<JsonElement?> LoadSummaryAsync()
    {
        var response = await _client.GetAsync("/api/diag/summary");
        // 404 is the documented "no report yet" state — nothing to assert against.
        if (response.StatusCode != HttpStatusCode.OK)
            return null;
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// /api/portfolio has always excluded this site from its own catalog; this projection did
    /// not, so the dashboard listed "PoPunkouterSoftware is unavailable" as the top attention
    /// item on the very page that was successfully serving it.
    /// </summary>
    [Fact]
    public async Task Summary_NeverReportsThisSiteAsNeedingAttention()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        const string self = "popunkoutersoftware";
        foreach (var item in summary.GetProperty("attentionItems").EnumerateArray())
        {
            var text = (item.GetString() ?? "").Replace("-", "").ToLowerInvariant();
            text.Should().NotContain(self, because: "the dashboard must not report on itself");
        }

        foreach (var point in summary.GetProperty("responseTimes").EnumerateArray())
        {
            var label = (point.GetProperty("label").GetString() ?? "").Replace("-", "").ToLowerInvariant();
            label.Should().NotContain(self);
        }
    }

    /// <summary>
    /// The hero badge rendered security + cleanup while its tooltip claimed it excluded only
    /// the staleness flag — so it silently dropped every unavailable service, the most
    /// actionable items on the page. The count is now computed server-side so the badge and
    /// the headline cannot disagree.
    /// </summary>
    [Fact]
    public async Task Summary_ActionableCount_IsAttentionCountMinusOnlyTheStalenessFlag()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        var attention = summary.GetProperty("attentionCount").GetInt32();
        var actionable = summary.GetProperty("actionableCount").GetInt32();
        var isStale = summary.GetProperty("isStale").GetBoolean();

        actionable.Should().Be(attention - (isStale ? 1 : 0));
        actionable.Should().BeGreaterThanOrEqualTo(
            summary.GetProperty("brokenServices").GetInt32(),
            because: "unavailable services are actionable and must be counted");
    }

    /// <summary>
    /// The compact first-paint contract must not carry fields no one renders.
    /// <c>serverErrors</c> was computed on every request and bound by nothing.
    /// </summary>
    [Fact]
    public async Task Summary_DoesNotCarryUnreadFields()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        summary.TryGetProperty("serverErrors", out _).Should().BeFalse();
        summary.TryGetProperty("webServices", out _).Should().BeFalse();
    }
}

[Collection("WebApp")]
public class PortfolioExclusionRegressionTests
{
    private readonly HttpClient _client;

    public PortfolioExclusionRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The card renders screenshot + name + description + PageSpeed chips + link. Everything
    /// else was payload serialised onto every app and read by nothing. Category is read by the
    /// page, not the card: it picks the section (web apps or RL training). GithubRepo is read by
    /// the Ctrl+K palette (repo and Actions rows).
    /// </summary>
    [Fact]
    public async Task Portfolio_Cards_CarryOnlyWhatTheCardBinds()
    {
        var response = await _client.GetAsync("/api/portfolio");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var app in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            app.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(
                new[] { "id", "name", "category", "description", "url", "status", "screenshotUrl", "scores", "cleanDays", "githubRepo" });
        }
    }
}

/// <summary>
/// The 404, which used to be JSON for everybody.
///
/// A single <c>MapFallback</c> answered every unmatched path with
/// <c>{"status":404,"path":"…","message":"Route not found."}</c> — so a browser that mistyped
/// a URL, or followed a link to an app that had been renamed, got that blob on a blank white
/// page: no header, no skip link, no way back. It also made the <c>&lt;NotFound&gt;</c> block in
/// Routes.razor unreachable, since a real navigation never reached the router.
///
/// One test for both halves, because they are one behaviour: the answer must depend on WHO is
/// asking, and the fix is what makes that possible. It is deliberately not two tests — the
/// integration tier is at its budget, and splitting this only creates two places to forget.
///
/// The status assertion is the one that matters most and the one that was wrong twice while
/// this was being built: a plain fallback gave a page-less 404, and a catch-all route gave the
/// right page with a 200. Both were caught here and by ProductionBootTests.
/// </summary>
[Collection("WebApp")]
public class NotFoundRouteRegressionTests
{
    private readonly HttpClient _client;

    public NotFoundRouteRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task UnmatchedRoutes_AnswerBrowsersWithTheAppsPage_AndApiClientsWithJson()
    {
        // ── A browser: the app's own chrome, at the original URL, with a real 404 ──
        foreach (var path in new[] { "/does-not-exist", "/azure/typo" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                because: $"{path} — a 200 tells a link checker, a monitor or a search engine that the page exists");
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
            body.Should().Contain("id=\"main-content\"",
                because: "the page must carry the skip-link target MainLayout points at, or 'Skip to content' goes nowhere");
            body.Should().Contain("<h1",
                because: "no h1 means <FocusOnNavigate Selector=\"h1\"> has nothing to focus on arrival");
            body.Should().NotContain("Route not found.",
                because: "that string is the old JSON fallback's body — it must not be what a browser sees");
        }

        // ── An API client: still JSON, still the requested path echoed back ──
        // Asserted here as well as in the E2E-API tier because this is the test that fails if
        // the /not-found route is ever turned into a catch-all, which is the one way the page
        // fix breaks the API contract.
        var api = await _client.GetAsync("/api/does-not-exist");
        api.StatusCode.Should().Be(HttpStatusCode.NotFound);
        api.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var doc = JsonDocument.Parse(await api.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("path").GetString().Should().Be("/api/does-not-exist");
    }
}
