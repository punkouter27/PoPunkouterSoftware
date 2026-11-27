using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace PoPunkouterSoftware.Integration;

public sealed class ManagementGateApp(Dictionary<string, string?>? extra = null, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;
        builder.UseEnvironment(environment);
        builder.UseSetting("KeyVault:Uri", "");
        builder.UseSetting("MicrosoftAuth:ClientId", extra?.GetValueOrDefault("MicrosoftAuth:ClientId") ?? "");
        builder.UseSetting("MicrosoftAuth:ClientSecret", extra?.GetValueOrDefault("MicrosoftAuth:ClientSecret") ?? "");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                ["Pinger:Enabled"] = "false",
                ["MicrosoftAuth:ClientId"] = "",
                ["MicrosoftAuth:ClientSecret"] = "",
            };
            foreach (var (key, value) in extra ?? [])
                settings[key] = value;
            cfg.AddInMemoryCollection(settings);
        });
    }
}

public class ManagementActionFilterTests
{
    [Theory]
    [InlineData(false, "1", null, HttpStatusCode.Forbidden)]
    [InlineData(true, null, null, HttpStatusCode.Forbidden)]
    [InlineData(true, "1", "https://example.invalid", HttpStatusCode.Forbidden)]
    [InlineData(true, "1", "https://localhost", HttpStatusCode.OK)]
    public async Task ManagementActions_RequireSessionFlagAndSameOriginHeader(
        bool enabled, string? header, string? origin, HttpStatusCode expected)
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = enabled.ToString(),
        }, environment: "Production");
        using var client = app.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/diag/cancel-refresh");
        if (header is not null)
            request.Headers.Add("X-Management-Request", header);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        (await client.SendAsync(request)).StatusCode.Should().Be(expected);
    }
}
