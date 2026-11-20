using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;
using Testcontainers.Azurite;

namespace PoPunkouterSoftware.Integration;

public class AzureReportStoreNoConnectionTests
{
    [Fact]
    public async Task LoadAsync_WhenConnectionStringIsEmpty_ReturnsFailureResult()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = "",
                ["AzureTableStorage:Endpoint"] = "",
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        var store = new AzureReportStore(logger, config);
        var result = await store.LoadAsync();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Table client not available");
    }
}

public class AzureReportStoreAzuriteTests : IAsyncLifetime
{
    // Built in InitializeAsync, not a field initializer: building needs a Docker endpoint, and a skipped
    // [DockerFact] must not touch Docker at all.
    private AzuriteContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [DockerFact]
    public async Task SaveThenLoad_RoundTrips_Report()
    {
        var store = BuildStore();
        var original = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "Test-Sub" },
            WebServices = new WebServicesInfo
            {
                Total = 2,
                Services = new List<WebService>
                {
                    new() { Name = "svc-a", Url = "https://svc-a.azurewebsites.net", HttpStatus = "200" },
                    new() { Name = "svc-b", Url = "https://svc-b.azurewebsites.net", HttpStatus = "404" }
                }
            }
        };

        await store.SaveAsync(original);
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        var loaded = result.Value;
        loaded.Should().NotBeNull();
        loaded!.Subscription!.Name.Should().Be("Test-Sub");
        loaded.WebServices!.Services.Should().HaveCount(2);
    }

    [DockerFact]
    public async Task SaveTwice_OnlyLatestDataRemains()
    {
        var store = BuildStore();
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "First" } });
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "Second" } });
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Subscription!.Name.Should().Be("Second");
    }

    [DockerFact]
    public async Task Load_EmptyTable_ReturnsNull()
    {
        var store = BuildStore();
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}

public class AzureReportStoreHistoryTests : IAsyncLifetime
{
    // Built in InitializeAsync, not a field initializer: building needs a Docker endpoint, and a skipped
    // [DockerFact] must not touch Docker at all.
    private AzuriteContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [DockerFact]
    public async Task SaveThenSave_HistoryContainsPreviousReport()
    {
        var store = BuildStore();

        var first = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-1" }
        };
        await store.SaveAsync(first);

        var second = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-2" }
        };
        await store.SaveAsync(second);

        var latest = await store.LoadAsync();
        latest.IsSuccess.Should().BeTrue();
        latest.Value!.Subscription!.Name.Should().Be("History-Sub-2");

        var history = await store.LoadHistoryAsync();
        history.IsSuccess.Should().BeTrue();
        history.Value.Should().NotBeNull();
        history.Value!.Should().Contain(r => r.Subscription!.Name == "History-Sub-1",
            because: "the previous report must be persisted to history on each save");
    }
}
