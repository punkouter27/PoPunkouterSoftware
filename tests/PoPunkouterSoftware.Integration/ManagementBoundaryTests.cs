using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Covers the <c>ManagementActionFilter</c> boundary — the no-auth site's only
/// server-side gate for mutating endpoints (<c>/api/diag/refresh</c> and
/// <c>/api/diag/cancel-refresh</c>). Each factory boots with explicit flag/key config
/// so the gate is tested in every state rather than whatever appsettings happens to say.
///
/// <para>Success paths drive <c>cancel-refresh</c> rather than <c>refresh</c>: cancelling
/// when nothing is running is a no-op that returns 200 without starting a real Azure scan,
/// so the gate is exercised without any outbound traffic.</para>
/// </summary>
public sealed class ManagementGateApp(Dictionary<string, string?>? extra = null, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment(environment);
        // See ProductionApp: the in-memory blank below arrives too late to stop the vault load.
        builder.UseSetting("KeyVault:Uri", "");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                ["Pinger:Enabled"] = "false",
            };
            foreach (var (key, value) in extra ?? [])
                settings[key] = value;
            cfg.AddInMemoryCollection(settings);
        });
    }
}

public class ManagementActionFilterTests
{
    private const string GatedEndpoint = "/api/diag/cancel-refresh";

    [Fact]
    public async Task FlagDisabled_ManagementEndpoints_Return403()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "false",
        });
        var client = app.CreateClient();

        // Restart touches a live site, so it must sit behind exactly the same gate as a scan.
        foreach (var path in new[] { "/api/diag/refresh", "/api/diag/restart/any-app" })
        {
            var resp = await client.PostAsync(path, content: null);
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task Production_KeyConfigured_MissingHeader_Returns401()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "expected-key",
        }, environment: "Production");
        var client = app.CreateClient();

        var resp = await client.PostAsync(GatedEndpoint, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The browser never holds the key, so it must not be offered buttons that can only 401.
        var cfg = await client.GetStringAsync("/api/config");
        cfg.Should().Contain("\"managementActionsEnabled\":false");
    }

    // ── Production fail-closed: flag on, no key ────────────────────────────────
    // EnableManagementActions is on in Production so a key holder can start a rescan by
    // hand. Without this guard, that config
    // would leave /api/diag/refresh open to anonymous callers — a free, repeatable,
    // ~30-second Azure subscription scan for anyone who finds the route.

    [Fact]
    public async Task Production_FlagEnabledWithoutKey_Returns403()
    {
        const string path = "/api/diag/refresh";
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "",
        }, environment: "Production");
        var client = app.CreateClient();

        var resp = await client.PostAsync(path, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a public deployment must not expose an unauthenticated Azure rescan");
    }

    [Fact]
    public async Task Production_FlagEnabledWithKeyAndHeader_Succeeds()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "nightly-key",
        }, environment: "Production");
        var client = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, GatedEndpoint);
        request.Headers.Add("X-Management-Key", "nightly-key");
        var resp = await client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a caller holding the key may start a rescan in Production");
    }

    [Fact]
    public async Task NonProduction_FlagEnabledWithoutKey_StillSucceeds()
    {
        // Local development has no key and needs none — the guard is Production-only.
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync(GatedEndpoint, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
