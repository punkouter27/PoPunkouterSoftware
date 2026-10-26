using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Integration;

[Collection("WebApp")]
public class HealthEndpointTests
{
    private readonly HttpClient _client;

    public HealthEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The whole /health contract in one request. This replaced eight Facts that each
    /// fetched the same document and looked at a different field of it — same coverage,
    /// eight fewer round trips through WebApplicationFactory, and a failure now names the
    /// contract rather than one property of it.
    /// </summary>
    [Fact]
    public async Task GetHealth_Returns200_WithTheFullDeepProbeContract()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.TryGetProperty("status", out _).Should().BeTrue();
        root.TryGetProperty("timestamp", out _).Should().BeTrue();
        root.TryGetProperty("checks", out _).Should().BeTrue();
        root.TryGetProperty("environment", out _).Should().BeTrue();
        root.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
        root.GetProperty("config").GetProperty("ASPNETCORE_ENVIRONMENT")
            .GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Static liveness — deliberately separate from the deep probe above.</summary>
    [Fact]
    public async Task GetLiveness_Returns200()
    {
        var response = await _client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Collection("WebApp")]
public class ConfigEndpointTests
{
    private readonly HttpClient _client;

    public ConfigEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The contract is exactly the three fields the WASM client's ConfigResponse binds.
    /// Anything else is payload nobody reads — this pins the response closed so it cannot
    /// silently regrow the discarded fields (isProduction, AI flags, model catalogue).
    /// Folded in from GetConfig_Returns200_WithAnAbsoluteApiBase to pay for the rate-limit
    /// test: same document, one more assertion.
    /// </summary>
    [Fact]
    public async Task GetConfig_ReturnsExactlyTheThreeFieldsTheClientBinds()
    {
        var response = await _client.GetAsync("/api/config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        doc.RootElement.GetProperty("apiBase").GetString()
            .Should().StartWith("http").And.EndWith("/api");

        doc.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("apiBase", "isMockMode", "managementActionsEnabled");

        // Folded in from the deleted GetConfig_TestEnvironment_ReportsMockMode: the exact
        // key set above already proves the auth-era fields (guestLoginEnabled,
        // microsoftOAuthEnabled) cannot regrow, so only the value claim was left to keep.
        doc.RootElement.GetProperty("isMockMode").GetBoolean().Should().BeTrue(
            because: "the hermetic fixture runs under the Testing environment");
    }
}

[Collection("WebApp")]
public class DiagReportEndpointTests
{
    private readonly HttpClient _client;

    public DiagReportEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The report endpoint is anonymous — the Advanced diagnostics panel is a WASM client
    /// with no credential — and it returns the whole inventory, resource ids included. Those
    /// ids carry the subscription GUID a few hundred times over. CLAUDE.md records that exact
    /// payload leaking once already from a served static file; moving it behind a route did
    /// not change who can read it, so the GUID is masked on the way out.
    /// </summary>
    [Fact]
    public async Task GetDiagReport_Returns200Or404_AndNeverCarriesTheSubscriptionId()
    {
        var response = await _client.GetAsync("/api/diag/report");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        if (response.StatusCode != HttpStatusCode.OK)
            return;

        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(TestWebApp.SeedSubscriptionId,
            because: "the subscription id must not reach an anonymous caller");
        // Asserted on the shape rather than on the seed's own GUID so this holds against any
        // report the endpoint might serve: not one /subscriptions/ segment anywhere in the
        // payload may be followed by a GUID.
        Regex.Matches(body, @"/subscriptions/(?<id>[^/""]+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["id"].Value)
            .Should().OnlyContain(id => id == "****",
                because: "every resource id must have its subscription segment masked");
        // The rest of the id survives: the panel renders resource group and name, and those
        // are already public in the hostnames the portfolio links to.
        body.Should().Contain("/subscriptions/****/resourceGroups/");
    }
}

[Collection("WebApp")]
public class OpsSummaryEndpointTests
{
    private readonly HttpClient _client;

    public OpsSummaryEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetSummary_ReturnsCompactStatusContract()
    {
        var response = await _client.GetAsync("/api/diag/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("healthPercent", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("attentionItems", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("responseTimes", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("webServices", out _).Should().BeFalse(
            because: "the first-paint summary must not return the full report graph");
    }
}

[Collection("WebApp")]
public class PortfolioEndpointTests
{
    private readonly HttpClient _client;

    public PortfolioEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetPortfolio_ReturnsStableDecoratedCatalog()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        apps.ValueKind.Should().Be(JsonValueKind.Array);
        apps.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var app in apps.EnumerateArray())
        {
            app.TryGetProperty("id", out _).Should().BeTrue();
            app.TryGetProperty("description", out var description).Should().BeTrue();
            description.GetString().Should().NotBeNullOrWhiteSpace();
            app.TryGetProperty("status", out _).Should().BeTrue();
        }

        // Every card has a README badge at /badge/<id>.svg, built from the same list — so the
        // badge can never describe an app the catalogue does not show. Unknown ids are 404s.
        var first = apps[0];
        var badge = await _client.GetAsync($"/api/portfolio/badge/{first.GetProperty("id").GetString()}.svg");
        badge.StatusCode.Should().Be(HttpStatusCode.OK);
        badge.Content.Headers.ContentType?.MediaType.Should().Be("image/svg+xml");
        badge.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromMinutes(5));
        var svg = await badge.Content.ReadAsStringAsync();
        svg.Should().StartWith("<svg").And.Contain(first.GetProperty("name").GetString()!);
        (await _client.GetAsync("/api/portfolio/badge/no-such-app.svg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The site must not showcase itself. Removing its apps.json entry is not sufficient —
    /// the response merges the catalog with EVERY service the Azure scan discovered, and this
    /// site is a real App Service in the same subscription, so it returns via the inventory
    /// path. Asserted on both the Azure resource name and the friendly name.
    /// </summary>
    [Fact]
    public async Task GetPortfolio_NeverIncludesThisSiteItself()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("apps").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString() ?? "")
            .ToList();

        names.Should().NotContain(n => n.Replace("-", "").Equals("PoPunkouterSoftware", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("app-popunkoutersoftware2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetPortfolio_EnvelopeCarriesFreshness_SoTheUiCanBeHonestAboutLive()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("stale", out var stale).Should().BeTrue();
        stale.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        doc.RootElement.TryGetProperty("refreshInProgress", out var refreshing).Should().BeTrue();
        refreshing.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task GetPortfolio_CardUrlsComeFromTheCuratedCatalog_NeverBlank()
    {
        // The catalog URL wins over scanned inventory so a card can never point a
        // visitor at a decommissioned host from a weeks-old report.
        var response = await _client.GetAsync("/api/portfolio");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var app in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            var url = app.GetProperty("url").GetString();
            url.Should().NotBeNullOrWhiteSpace();
            Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue(
                because: $"'{app.GetProperty("name").GetString()}' must have an absolute URL");
        }
    }
}


