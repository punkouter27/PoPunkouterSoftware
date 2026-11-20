using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;
using System.Net;
using System.Text.Json;
using Testcontainers.Azurite;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Full in-process host wired to a REAL Azurite table service: the only storage config the
/// host sees is the container's connection string, so the /api/diag read endpoints exercise
/// the genuine Table Storage path (gzip round-trip, history rows, summary rows) end to end.
/// The container starts in InitializeAsync BEFORE any host creation — safe because the
/// WebApplicationFactory host builds lazily on the first CreateClient/Services access.
/// </summary>
public sealed class AzuriteWebApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Built only when Docker answers: xunit creates a class fixture even when every test using it is skipped,
    // and building a container needs a Docker endpoint.
    private readonly AzuriteContainer? _azurite = Docker.IsAvailable
        ? new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build()
        : null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Same hermetic-run keys as TestWebAppFixture: both Key Vault keys must be
                // cleared or the real shared vault loads and leaks secrets.
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["Pinger:Enabled"] = "false",
                ["AzureTableStorage:ConnectionString"] = _azurite?.GetConnectionString() ?? "",
            });
        });
    }

    Task IAsyncLifetime.InitializeAsync() => _azurite?.StartAsync() ?? Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Tear the host down first so nothing is still talking to storage, then the container.
        await base.DisposeAsync();
        if (_azurite is not null)
            await _azurite.DisposeAsync();
    }
}

public class AzuriteWebAppTests : IClassFixture<AzuriteWebApp>
{
    private readonly AzuriteWebApp _app;

    public AzuriteWebAppTests(AzuriteWebApp app) => _app = app;

    private async Task<AzureReport> SeedReportAsync()
    {
        var report = new AzureReport
        {
            GeneratedAt = DateTime.UtcNow,
            Subscription = new SubscriptionInfo { Name = "Azurite-Seeded-Sub" },
            WebServices = new WebServicesInfo
            {
                Total = 2,
                ByStatus = new ByStatusInfo { Active = 1, Broken = 1 },
                Services = new List<WebService>
                {
                    new()
                    {
                        Name = "app-po-alpha",
                        FriendlyName = "PoAlpha",
                        Url = "https://po-alpha.azurewebsites.net",
                        HttpStatus = ServiceHealth.Active,
                        Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 42 },
                    },
                    new()
                    {
                        Name = "app-po-beta",
                        FriendlyName = "PoBeta",
                        Url = "https://po-beta.azurewebsites.net",
                        HttpStatus = ServiceHealth.Broken,
                        Connectivity = new ConnectivityInfo { Success = false, Error = "503" },
                    },
                },
            },
        };

        // AzureReportStore is a singleton in Program.cs — the endpoints read the same instance.
        var store = _app.Services.GetRequiredService<AzureReportStore>();
        var saved = await store.SaveAsync(report);
        saved.IsSuccess.Should().BeTrue(because: "seeding through the real store must succeed against Azurite");
        return report;
    }

    [DockerFact]
    public async Task SeededReport_IsServedBy_DiagReportEndpoint()
    {
        await SeedReportAsync();
        var client = _app.CreateClient();

        var resp = await client.GetAsync("/api/diag/report");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("subscription").GetProperty("name").GetString()
            .Should().Be("Azurite-Seeded-Sub");
        doc.RootElement.GetProperty("webServices").GetProperty("total").GetInt32().Should().Be(2);
    }

    [DockerFact]
    public async Task DiagSummary_Returns200_WithComputedHealthPercent()
    {
        await SeedReportAsync();
        var client = _app.CreateClient();

        var resp = await client.GetAsync("/api/diag/summary");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var healthPercent = doc.RootElement.GetProperty("healthPercent").GetInt32();
        healthPercent.Should().Be(50, because: "the seeded report has 1 active of 2 total services");
        doc.RootElement.GetProperty("subscriptionName").GetString().Should().Be("Azurite-Seeded-Sub");
    }
}

