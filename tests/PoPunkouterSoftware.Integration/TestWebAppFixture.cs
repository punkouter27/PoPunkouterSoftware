using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Integration;

public class TestWebApp : WebApplicationFactory<Program>
{
    /// <summary>
    /// The report-cache file this fixture seeds, so /api/diag/report and /api/diag/summary
    /// have something to project.
    /// <para>These tests used to get a report for free from
    /// <c>Client/wwwroot/data/azure-full-report.json</c> — a committed snapshot of a real
    /// production scan, which was also being served to the public internet as a static file
    /// and has since been removed. The dependency was never declared; blanking
    /// <c>AzureTableStorage:ConnectionString</c> below only looked hermetic because a
    /// checked-in data dump was quietly backfilling it. The fixture now owns its own
    /// fixture data, which is what "hermetic" was supposed to mean.</para>
    /// </summary>
    private string? _seededReportPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable Key Vault for hermetic tests. Program.cs checks "KeyVault:Uri"
                // BEFORE "AzureKeyVaultUri", so both must be cleared or the real shared
                // vault loads and leaks secrets (e.g. Authentication:Microsoft:ClientId).
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                // appsettings.json now carries a real blob endpoint so production can serve
                // the nightly screenshots. Blank it here or every /api/portfolio call in this
                // tier tries to reach live Azure storage on a credential it does not have.
                ["AzureBlobStorage:Endpoint"] = "",
                // Same reason, one endpoint over: appsettings.json names the real shared
                // Application Insights component so /users can read the sign-in roster in
                // production. Blank here, or every /api/signins call in this tier walks the
                // DefaultAzureCredential chain against live Azure on a credential it does not
                // have — slow, non-hermetic, and dependent on whether the developer happens to
                // be logged in with `az`. Unset is the deterministic "unavailable" branch.
                ["SignIns:ResourceId"] = "",
            });
        });

    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        SeedReportCache(host);
        return host;
    }

    /// <summary>Set aside while the seed is in place, and put back on dispose.</summary>
    private string? _displacedReportPath;

    private void SeedReportCache(IHost host)
    {
        var env = host.Services.GetRequiredService<IWebHostEnvironment>();
        var path = ReportFileCache.EnsureReportPath(env);

        // The seed goes in unconditionally.
        //
        // This used to bail out when a file was already there ("never clobber a real cache"),
        // which meant a developer who had run the app locally ran this tier against their own
        // App_Data report instead of the fixture's — different service names, different
        // counts, a real subscription id. Tests that asserted on seeded values passed only
        // because they happened not to look at the differing fields. A fixture that silently
        // defers to ambient machine state is the same undeclared dependency the committed
        // wwwroot report was, one directory over.
        //
        // Only the FIRST host displaces. A run builds several hosts, and the second one finds
        // the first one's seed in place: moving that aside with overwrite clobbered the real
        // report the first host had set aside, so a test run deleted the developer's local
        // cache outright (2026-11-03). A seed already displaced is never displaced again.
        var displaced = path + ".displaced-by-tests";
        if (File.Exists(path) && !File.Exists(displaced))
        {
            File.Move(path, displaced);
            _displacedReportPath = displaced;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(BuildSeedReport(), SeedJsonOptions));
        _seededReportPath = path;
    }

    private static readonly JsonSerializerOptions SeedJsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A deliberately small but REALISTIC report: one healthy service and one broken one, so
    /// the projections under test (health percent, attention items, response times, the
    /// scan delta) all have something non-trivial to compute from. A report of zero services
    /// would make several assertions pass vacuously.
    /// </summary>
    /// <summary>The GUID the seeded resource ids carry, asserted absent from /api/diag/report.</summary>
    public const string SeedSubscriptionId = "11111111-2222-3333-4444-555555555555";

    private static AzureReport BuildSeedReport() => new()
    {
        GeneratedAt = DateTime.UtcNow,
        Subscription = new SubscriptionInfo { Name = "TestSubscription" },
        WebServices = new WebServicesInfo
        {
            Total = 2,
            ByStatus = new ByStatusInfo { Active = 1, Broken = 1, Other = 0 },
            Services = new List<WebService>
            {
                new()
                {
                    Name = "app-seed-healthy",
                    FriendlyName = "SeedHealthy",
                    ResourceGroup = "rg-seed",
                    ResourceType = "Microsoft.Web/sites",
                    // A realistic ARM id, because the subscription GUID inside one is what
                    // /api/diag/report has to mask before it answers an anonymous caller.
                    ResourceId = $"/subscriptions/{SeedSubscriptionId}/resourceGroups/rg-seed/providers/Microsoft.Web/sites/app-seed-healthy",
                    Url = "https://app-seed-healthy.example.net",
                    HttpStatus = ServiceHealth.Active,
                    PlatformState = "Running",
                    Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 143 },
                },
                new()
                {
                    Name = "app-seed-broken",
                    FriendlyName = "SeedBroken",
                    ResourceGroup = "rg-seed",
                    ResourceType = "Microsoft.Web/sites",
                    ResourceId = $"/subscriptions/{SeedSubscriptionId}/resourceGroups/rg-seed/providers/Microsoft.Web/sites/app-seed-broken",
                    Url = "https://app-seed-broken.example.net",
                    HttpStatus = ServiceHealth.Broken,
                    PlatformState = "Running",
                    Connectivity = new ConnectivityInfo { Success = false, ResponseTime = 2100, Error = "503" },
                },
            },
        },
        Cost = new CostInfo
        {
            TotalFormatted = "$12.34",
            TopCostDrivers = new List<CostDriver> { new() { Name = "rg-seed", Cost = 12.34 } },
        },
        AllResourceSummary = new AllResourceSummaryInfo { Total = 7 },
    };

    protected override void Dispose(bool disposing)
    {
        // Runs once. WebApplicationFactory reaches Dispose(true) from both DisposeAsync and
        // Dispose, and the second pass deleted the REAL report the first had just restored —
        // a test run wiped the developer's local cache (2026-11-03). The fields are cleared
        // before anything can throw, so a second call finds nothing to do.
        if (disposing && _seededReportPath is { } seeded)
        {
            var displaced = _displacedReportPath;
            _seededReportPath = _displacedReportPath = null;
            try
            {
                File.Delete(seeded);
                if (displaced is not null && File.Exists(displaced))
                    File.Move(displaced, seeded, overwrite: true);
            }
            catch (IOException) { /* best effort */ }
        }

        base.Dispose(disposing);
    }
}

[CollectionDefinition("WebApp")]
public class WebAppCollection : ICollectionFixture<TestWebApp>
{
}
