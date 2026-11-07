using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.E2EAPI;

/// <summary>
/// Pure HTTP smoke tests against a LIVE host — plain HttpClient, no WebApplicationFactory,
/// no browser. Target comes from BASE_URL (defaults to a local run on :8000). Every assertion
/// must hold against BOTH a fresh localhost instance and production, so no test asserts an
/// environment name and data-dependent endpoints allow their documented degraded codes.
/// </summary>
public sealed class ApiSmokeFixture : IDisposable
{
    public HttpClient Client { get; } = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000"),
        // Production cold starts on a small App Service plan can take tens of seconds.
        Timeout = TimeSpan.FromSeconds(60),
    };

    public void Dispose() => Client.Dispose();
}

public class ApiSmokeTests : IClassFixture<ApiSmokeFixture>
{
    private readonly HttpClient _client;

    public ApiSmokeTests(ApiSmokeFixture fixture) => _client = fixture.Client;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

    // ─── Broad availability sweep ─────────────────────────────────────────────

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health")]
    [InlineData("/api/config")]
    [InlineData("/api/portfolio")]
    [InlineData("/robots.txt")]
    // /openapi/v1.json and /scalar/v1 are deliberately NOT here: they are mapped only
    // outside Production, so "available on any live host" is no longer true of them.
    // Their absence in Production is asserted by Api_DoesNotPublishItsOwnSchemaInProduction.
    public async Task PublicGetEndpoint_Returns200(string path)
    {
        var resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{path} must be available on any live host");
    }

    // ─── Health ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Liveness_Returns200_WithOkStatus()
    {
        var resp = await _client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    /// <summary>
    /// The whole /health contract, including the rule that decides whether the masked config
    /// block is there at all. Two Facts fetched the same document to look at different halves
    /// of it; they are one request now.
    ///
    /// <para>The config block is a development diagnostic and is OMITTED in Production —
    /// /health is anonymous, and on the live host that block published the environment name,
    /// which settings are bound, and a masked-but-suffixed Key Vault URI whose last four
    /// characters name the vault. So this asserts "absent, or present and fully masked",
    /// which is the contract on every host this tier can be pointed at.</para>
    /// </summary>
    [Fact]
    public async Task Health_Returns200_WithChecks_AndConfigOnlyWhenNotProduction()
    {
        var resp = await _client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
        doc.RootElement.TryGetProperty("status", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("checks", out var checks).Should().BeTrue();
        checks.ValueKind.Should().Be(JsonValueKind.Object);

        // Every dependency check must render its verdict, whatever that verdict is.
        foreach (var check in checks.EnumerateObject())
            check.Value.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();

        var hasConfig = doc.RootElement.TryGetProperty("config", out var config)
                        && config.ValueKind == JsonValueKind.Object;
        if (!hasConfig)
            return; // Production: the block is gone, which is the point.

        // The environment name is the one deliberately unmasked key; assert it exists
        // (but never assert its VALUE — this must pass on localhost and production alike).
        config.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var envName).Should().BeTrue();
        envName.GetString().Should().NotBeNullOrWhiteSpace();

        foreach (var prop in config.EnumerateObject().Where(p => p.Name != "ASPNETCORE_ENVIRONMENT"))
        {
            var value = prop.Value.GetString();
            // Two masking strategies are in play, and both count as masked:
            //   SecretMasking.MaskValue  -> "(not set)" / "****" / "abcd****wxyz"
            //   the App Insights sentinel -> "configured (redacted)", which never reveals
            //     even the first four characters of a connection string.
            var isMasked = value is "(not set)" or "****" or "configured (redacted)"
                           || (value?.Contains('*') ?? false);
            isMasked.Should().BeTrue(because: $"config key '{prop.Name}' must be masked, got '{value}'");
        }
    }

    // ─── Hardening ────────────────────────────────────────────────────────────

    /// <summary>
    /// The security headers used to be declared in wwwroot/staticwebapp.config.json — Static
    /// Web Apps configuration, in an App Service app, read by nothing. Production sent none
    /// of them. They come from Host/SecurityHeaders.cs now, and this is the assertion that
    /// would have caught the gap: it reads the response, not the config file.
    /// </summary>
    [Fact]
    public async Task EveryResponse_CarriesTheSecurityHeaders()
    {
        var resp = await _client.GetAsync("/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var headers = resp.Headers;

        headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        headers.GetValues("Referrer-Policy").Should().ContainSingle();
        headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");

        var csp = headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("object-src 'none'");
        // The whole reason App.razor's boot scripts are external files. If someone re-inlines
        // one, the honest fix is to move it back out — not to widen this. Asserted on the
        // script-src directive alone: style-src legitimately carries 'unsafe-inline'
        // (Radzen and Blazor both write inline style attributes), so a substring search
        // across the whole policy would silently pass on the wrong directive.
        var scriptSrc = csp.Split(';')
            .Select(d => d.Trim())
            .Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        scriptSrc.Should().Be("script-src 'self' 'wasm-unsafe-eval'");
    }

    /// <summary>
    /// /openapi/v1.json and /scalar/v1 were mapped unconditionally, publishing the full route
    /// table — management routes included — of an app with no login. They are development
    /// tooling and must not answer on the live host.
    /// </summary>
    [Fact]
    public async Task Api_DoesNotPublishItsOwnSchemaInProduction()
    {
        var health = await ReadJsonAsync(await _client.GetAsync("/health"));
        var isProduction = health.RootElement.GetProperty("environment").GetString() == "Production";
        if (!isProduction)
            return; // locally these are mapped on purpose

        foreach (var path in new[] { "/openapi/v1.json", "/scalar/v1" })
        {
            var resp = await _client.GetAsync(path);
            ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                because: $"{path} must not answer on a production host");
        }
    }

    // ─── Config ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_Returns200_WithApiBaseEndingInApi()
    {
        var resp = await _client.GetAsync("/api/config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("apiBase").GetString().Should().EndWith("/api");
    }

    [Fact]
    public async Task Config_HasNoAuthFlags_SiteIsNoAuthByDesign()
    {
        var resp = await _client.GetAsync("/api/config");

        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("guestLoginEnabled", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("microsoftOAuthEnabled", out _).Should().BeFalse();
    }

    /// <summary>
    /// /api/config is exactly the three fields the WASM client binds — see the matching
    /// integration test. Pinned closed so the response cannot regrow unread payload.
    /// </summary>
    [Fact]
    public async Task Config_ReturnsExactlyTheThreeFieldsTheClientBinds()
    {
        var resp = await _client.GetAsync("/api/config");

        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("apiBase", "isMockMode", "managementActionsEnabled");
    }

    // ─── Portfolio ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Portfolio_ReturnsNonEmptyEnvelope_EveryItemHasIdStatusDescription()
    {
        var resp = await _client.GetAsync("/api/portfolio");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("stale", out _).Should().BeTrue(
            because: "the UI needs inventory freshness to render honest Live badges");
        var apps = doc.RootElement.GetProperty("apps");
        apps.ValueKind.Should().Be(JsonValueKind.Array);
        apps.GetArrayLength().Should().BeGreaterThan(0,
            because: "the catalog stays visible even with no Azure report");

        foreach (var item in apps.EnumerateArray())
        {
            item.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// The original "ghost fleet" bug: cards pointing at decommissioned hosts. Every
    /// rendered card URL must actually resolve and answer. Network-bound by design —
    /// this is the E2E tier, which already requires a running app and real DNS.
    /// </summary>
    [Fact]
    public async Task Portfolio_EveryCardUrl_ActuallyResolvesAndResponds()
    {
        var resp = await _client.GetAsync("/api/portfolio");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);

        // A browser User-Agent, because a browser is what actually follows these links: the
        // card is an <a href> a visitor clicks. Without one, any app behind an anti-scraping
        // guard answers 400 and reads as a ghost — PoSeeReview does exactly that, and it is
        // very much alive. The point of this test is "does the host exist and answer", so the
        // probe has to look like the client the card is built for.
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        probe.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        var ghosts = new List<string>();
        var mislabelled = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var url = item.GetProperty("url").GetString();
            var status = item.GetProperty("status").GetString();
            try
            {
                // Cold F1 apps 200 slowly; any HTTP answer proves the host exists.
                using var r = await probe.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                var code = (int)r.StatusCode;

                // A host that answers 404/410 for its own root is a ghost: the DNS name
                // survives, the app does not.
                if (code is 404 or 410)
                    ghosts.Add($"{name}: {url} -> {code}");

                // Anything else >= 400 means the app is up and refusing — that is a real
                // outage from a visitor's point of view, and the card must SAY so rather
                // than be absent. This is the assertion that used to fail the whole suite
                // whenever one of 11 third-party apps had a bad night: it asserted the app
                // was healthy, when the contract this repo actually owns is that the card
                // tells the truth about it.
                else if (code >= 400 && status != "unavailable")
                    mislabelled.Add($"{name}: {url} -> {code} but card says '{status}'");
            }
            catch (Exception ex)
            {
                ghosts.Add($"{name}: {url} -> {ex.GetBaseException().Message}");
            }
        }

        ghosts.Should().BeEmpty(because: "the portfolio must not showcase apps that no longer exist");
        mislabelled.Should().BeEmpty(because: "a card linking to a failing app must be marked unavailable");
    }

    // ─── Diag ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiagSummary_Returns200Or404_ShapeIsOpsSummaryNotRawReport()
    {
        var resp = await _client.GetAsync("/api/diag/summary");

        // 404 is the documented "no report yet" state on a fresh host.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        if (resp.StatusCode != HttpStatusCode.OK)
            return;

        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("healthPercent", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("attentionItems", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("responseTimes", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("webServices", out _).Should().BeFalse(
            because: "the summary is a projection, never the raw report graph");
    }

    [Fact]
    public async Task DiagReport_Returns200Or404Or503_SuccessIsJson()
    {
        var resp = await _client.GetAsync("/api/diag/report");

        // 404 = no report yet; 503 = table storage unavailable with no file cache.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
        if (resp.StatusCode == HttpStatusCode.OK)
            resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    // ─── Host plumbing ────────────────────────────────────────────────────────

    [Fact]
    public async Task RobotsTxt_Returns200_TextPlain_WithUserAgentDirective()
    {
        var resp = await _client.GetAsync("/robots.txt");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("User-agent");
    }

    [Fact]
    public async Task Favicon_Returns200_WithImageContentType()
    {
        // /favicon.ico 302-redirects to the static asset; HttpClient follows it.
        var resp = await _client.GetAsync("/favicon.ico");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");
    }

    // ─── Errors and API docs ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api/nope/deeper/route")]
    public async Task UnknownApiRoute_Returns404_WithJsonStatusAndPath(string path)
    {
        var resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("path").GetString().Should().Be(path);
    }

    [Fact]
    public async Task OpenApiDocument_Returns200_AndParsesAsJson()
    {
        var resp = await _client.GetAsync("/openapi/v1.json");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("openapi", out _).Should().BeTrue();
    }
}

