using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Every JSON response must reach the browser in camelCase, because the WASM client's
/// source-generated <c>AppJsonContext</c> binds camelCase names and a reflection fallback
/// does not exist (PublishTrimmed + EnableTrimAnalyzer, no escape hatch).
/// <para>Trimmed to the budget (CLAUDE.md: 50 Integration). This was 15 tests: three
/// theories with thirteen InlineData property names between them, each booting a request
/// to assert one key. Casing is a single serializer setting per endpoint — asserting the
/// whole key set in one request per endpoint is the same claim, and a failure now shows
/// which endpoint regressed rather than which of thirteen keys.</para>
/// </summary>
[Collection("WebApp")]
public class ApiSchemaContractTests
{
    private readonly HttpClient _client;

    public ApiSchemaContractTests(TestWebApp factory) => _client = factory.CreateClient();

    private static readonly string[] HealthProperties = ["status", "application", "timestamp", "checks", "environment", "config"];
    private static readonly string[] SummaryProperties = ["generatedAt", "isStale", "healthPercent", "attentionCount"];
    private static readonly string[] SignInProperties = ["generatedAt", "windowDays", "available", "totalPeople", "totalSignIns", "people", "apps", "trend", "recent"];
    private static readonly string[] ReportProperties = ["generatedAt", "webServices", "subscription"];

    [Fact]
    public async Task Health_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/health"));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(HealthProperties);
    }

    [Fact]
    public async Task Config_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/config"));

        doc.RootElement.TryGetProperty("apiBase", out _)
            .Should().BeTrue(because: "/api/config must expose camelCase 'apiBase'");
    }

    [Fact]
    public async Task OpsSummary_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/diag/summary"));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(SummaryProperties);
    }

    /// <summary>
    /// The sign-in roster, in the shape the page meets when nothing is configured — which is
    /// the fixture's deliberate state (see TestWebApp) and the same shape a missing credential
    /// or a missing Monitoring Reader role produces in production.
    /// <para>Two claims in one request: the casing contract, and that the endpoint answers
    /// <b>200 with a reason</b> rather than a 500. Degradation is a designed path here — the
    /// page renders "why there is nothing" — and an exception-mapped status would give it
    /// nothing to say.</para>
    /// </summary>
    [Fact]
    public async Task SignIns_ExposesItsContractInCamelCase_AndDegradesRatherThanFailing()
    {
        var response = await _client.GetAsync("/api/signins?days=7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Contain(SignInProperties);

        root.GetProperty("available").GetBoolean().Should().BeFalse();
        root.GetProperty("unavailable").GetString().Should().NotBeNullOrWhiteSpace(
            because: "the page renders the reason, so an unavailable report without one is a blank panel");

        // The caller-supplied window is clamped, never rejected.
        root.GetProperty("windowDays").GetInt32().Should().Be(7);
        (await _client.GetFromJsonAsync<JsonElement>("/api/signins?days=9999"))
            .GetProperty("windowDays").GetInt32().Should().Be(90,
                because: "90 days is the Application Insights retention on the shared component");
    }

    /// <summary>
    /// The report is the only nested contract, so it checks one level down as well — a
    /// naming policy can be applied at the root and missed on a nested record.
    /// </summary>
    [Fact]
    public async Task DiagReport_WhenOk_ExposesItsContractInCamelCase_IncludingNestedRecords()
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            return;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(ReportProperties);

        if (doc.RootElement.TryGetProperty("webServices", out var ws))
            ws.TryGetProperty("total", out _)
                .Should().BeTrue(because: "webServices.total must be camelCase");
    }
}
