using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoPunkouterSoftware.Shared;
using System.Net.Http.Json;
using PoPunkouterSoftware.Api;
using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Boots the real entry point under <c>Production</c> — the one environment nothing else
/// covered, and the one where the app was broken.
///
/// <para><see cref="FakeAuthHandler"/>'s constructor throws on Production initialization as a
/// deliberate guardrail. That guardrail is only safe if the registration is conditional; it
/// was not. The scheme was added unconditionally AND named as the default, so
/// <c>UseAuthentication</c> resolved it on every single request, the constructor threw, and
/// every page and endpoint answered 500. The comment above the registration claimed the
/// opposite, so nothing looked wrong on inspection.</para>
///
/// <para>Key Vault, Table Storage and App Insights are all blanked: this is about the
/// hosting pipeline, and Program.cs reads a blank vault URI as "do not bind", so no real
/// Azure resource is contacted.</para>
/// </summary>
public sealed class ProductionApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Production");
        // Program.cs reads the vault URI while the builder is still being created — before
        // the in-memory overrides below apply — so only UseSetting keeps this boot off the
        // real kv-poshared2 (it was loading every secret in it through the dev's az login).
        builder.UseSetting("KeyVault:Uri", "");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                ["AzureBlobStorage:Endpoint"] = "",
                ["Pinger:Enabled"] = "false",
                // /api/diag's live probes of GitHub, nuget.org, the AI endpoint and friends stay off:
                // a test must not call the internet. The rows still appear, saying so.
                ["Diag:ProbeServices"] = "false",
                // Unset = the instant "unavailable" branch, rather than a credential-chain walk
                // against live Azure on every call the rate-limit test makes.
                ["SignIns:ResourceId"] = "",
                // Low enough to trip in one test; nothing else in this class calls /api/signins.
                ["RateLimits:SignInsPerMinute"] = "2",
            }));
    }
}

public class ProductionBootTests : IClassFixture<ProductionApp>
{
    private readonly HttpClient _client;
    private readonly ProductionApp _factory;

    public ProductionBootTests(ProductionApp app)
    {
        _factory = app;
        _client = app.CreateClient();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/config")]
    [InlineData("/api/portfolio")]
    public async Task PublicEndpoints_DoNotFailWithServerError(string path)
    {
        var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        // The body is in the message on purpose: a bare "expected < 500 but found 503" from
        // /health names no check, and the whole point of that endpoint is to say which
        // dependency is unhappy.
        ((int)response.StatusCode).Should().BeLessThan(500,
            because: $"{path} must serve in Production, not throw out of the auth pipeline. Body: {body}");
    }

    /// <summary>
    /// The anonymous live-query endpoint has a ceiling in Production, and hitting it is a clean
    /// 429 with a Retry-After — not a 500, and not an unlimited Log Analytics bill.
    /// </summary>
    [Fact]
    public async Task AnonymousLiveQueryEndpoint_IsRateLimited_WithRetryAfter()
    {
        (await _client.GetAsync("/api/signins")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/signins")).StatusCode.Should().Be(HttpStatusCode.OK);

        var limited = await _client.GetAsync("/api/signins");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter?.Delta.Should().BeGreaterThan(TimeSpan.Zero);
    }

    /// <summary>
    /// With no FakeAuth scheme registered, management actions must still fail closed —
    /// and fail *cleanly*, with a 4xx rather than an unhandled exception.
    /// </summary>
    [Fact]
    public async Task ManagementEndpoints_FailClosed_WithoutServerError()
    {
        const string path = "/api/diag/refresh";
        var response = await _client.PostAsync(path, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Everything about a Production response that a live probe found wrong at once: the
    /// schema endpoints answered, /health published its masked config block, and not one of
    /// the four security headers was sent because they were declared in
    /// wwwroot/staticwebapp.config.json — Static Web Apps configuration in an App Service
    /// app, read by nothing.
    ///
    /// <para>One test rather than four: they share a boot of the Production pipeline, which
    /// is the expensive part, and a failure in any of them means the same thing — this
    /// environment is not hardened.</para>
    /// </summary>
    [Fact]
    public async Task ProductionResponses_AreHardened()
    {
        // 1. No schema publication.
        foreach (var path in new[] { "/openapi/v1.json", "/scalar/v1" })
        {
            var schema = await _client.GetAsync(path);
            ((int)schema.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                because: $"{path} is development tooling and must not answer in Production");
        }

        // 2. /health is anonymous here, so it must not carry the config block.
        var health = await _client.GetAsync("/health");
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        var hasConfig = doc.RootElement.TryGetProperty("config", out var config)
                        && config.ValueKind == JsonValueKind.Object;
        hasConfig.Should().BeFalse(because: "the masked config block is a development diagnostic");

        // 3. The security headers ship from middleware now, on every response.
        var page = await _client.GetAsync("/health/live");
        page.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        page.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        page.Headers.GetValues("Referrer-Policy").Should().ContainSingle();
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("script-src 'self' 'wasm-unsafe-eval'");
    }

    /// <summary>
    /// Validators are registered one by one (Program.cs), not by an assembly scan, so a trimmed or
    /// reordered assembly cannot silently drop one. Both endpoints that validate resolve theirs here.
    /// </summary>
    [Fact]
    public void Validators_AreRegisteredByType()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<FluentValidation.IValidator<SignInSearchFilter>>()
            .Should().BeOfType<SignInSearchFilterValidator>();
        scope.ServiceProvider.GetRequiredService<FluentValidation.IValidator<AiTriageAdHocRequest>>()
            .Should().BeOfType<AiTriageAdHocRequestValidator>();
    }

    /// <summary>
    /// <c>/api/diag</c> (DiagReportDto) feeds the anonymous <c>/diag</c> page, so in Production it must
    /// carry verdicts and nothing else: none of what got the first /diag deleted on 2026-09-04
    /// (ContentRoot path, config block, suffixed connection strings) and none of what the diag
    /// standard forbids (URIs, keys, ids, emails, exception messages, stack traces). Asserted
    /// over the whole serialized body so a field added later cannot slip past.
    /// </summary>
    [Fact]
    public async Task DiagStatus_IsFriendlyAndLeaksNothing_InProduction()
    {
        var response = await _client.GetAsync("/api/diag");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("environment").GetString().Should().Be("Production");
        root.GetProperty("version").GetString().Should().MatchRegex("^[0-9]{6}$");
        root.TryGetProperty("checkedMs", out _).Should().BeTrue();
        root.TryGetProperty("config", out _).Should().BeFalse();
        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        checks.Select(c => c.GetProperty("name").GetString()).Should()
            .Contain(["server", "KeyVault", "TableStorage", "AzureInventory", "ApplicationInsights", "ai", "screenshots", "alerts",
                "AzureResourceManager", "GitHub", "NuGet", "OSV", "AzurePrices", "RDAP"])
            .And.OnlyHaveUniqueItems();
        checks.Select(c => c.GetProperty("group").GetString()).Should().OnlyContain(g => g == "api" || g == "data" || g == "service");
        checks.Should().OnlyContain(c => c.GetProperty("label").GetString()!.Length > 0
                                         && c.GetProperty("detail").GetString()!.Length > 0
                                         && c.GetProperty("purpose").GetString()!.Length > 0
                                         && (new[] { "Healthy", "Off" }.Contains(c.GetProperty("status").GetString())
                                             || c.GetProperty("problem").GetString()!.Length > 0));

        // Outage pushes are opt-in (no Alerts:NtfyTopic here): the row is "Off" — neutral, no problem
        // text, nothing called — and an Off row never drags the overall status down.
        var alerts = checks.Single(c => c.GetProperty("name").GetString() == "alerts");
        alerts.GetProperty("status").GetString().Should().Be("Off");
        alerts.GetProperty("problem").ValueKind.Should().Be(JsonValueKind.Null);
        alerts.GetProperty("latencyMs").GetDouble().Should().Be(0);
        if (checks.Where(c => c.GetProperty("group").GetString() != "api")
            .All(c => c.GetProperty("status").GetString() is "Healthy" or "Off"))
            root.GetProperty("status").GetString().Should().Be("Healthy");

        var contentRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        body.Should().NotContain(contentRoot.Replace("\\", "\\\\"))
            .And.NotContain(contentRoot)
            .And.NotContainAny("://", "AccountKey", "SharedAccessSignature", "InstrumentationKey",
                "Endpoint=", "vault.azure", "core.windows", "/subscriptions/", "@", "Exception:", "   at ");
        body.Should().NotMatchRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        body.Should().NotMatchRegex(@"[A-Za-z]:\\\\");

        // The Rewrite button's FluentValidation rules also guard the endpoint: an oversized
        // list is a 400 validation problem, never a paid completion.
        var tooMany = Enumerable.Repeat("x", AiTriageAdHocRequestValidator.MaxItems + 1).ToArray();
        var rewrite = await _client.PostAsJsonAsync("/api/diag/ai", new { attentionItems = tooMany });
        rewrite.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rewrite.Content.ReadAsStringAsync()).Should().Contain("\"errors\"").And.Contain("AttentionItems");
    }

    /// <summary>
    /// The X-Fake-* headers must buy nothing in Production. The scheme is absent, so they
    /// are inert request headers — but the response must still be a clean 4xx.
    /// </summary>
    [Fact]
    public async Task FakeAuthHeaders_GrantNothing_InProduction()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/diag/refresh");
        request.Headers.Add(FakeAuthHandler.UserHeader, "admin");
        request.Headers.Add(FakeAuthHandler.RolesHeader, FakeAuthHandler.ManagementRole);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
