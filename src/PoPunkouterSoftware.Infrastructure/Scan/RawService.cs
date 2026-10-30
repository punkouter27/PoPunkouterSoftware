using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Intermediary representation used during a single scan. Accumulates connectivity,
/// metrics, and free-tier check results before being projected to the immutable
/// <see cref="WebService"/> record returned in the report.
/// </summary>
internal record RawService
{
    public string Name { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string ResourceTypeRaw { get; init; } = "";
    public string? Url { get; init; }
    public string? Sku { get; init; }
    public string? PlatformState { get; init; }
    public string? ResourceId { get; init; }
    public ConnectivityInfo? Connectivity { get; init; }
    public MetricsInfo? Metrics7Days { get; init; }
    public FreeTierCheckInfo? FreeTierCheck { get; init; }
    public string HttpStatus { get; init; } = "unknown";
    public string? Kind { get; init; }
    public string? AppServicePlan { get; init; }
    public string? AppServicePlanSku { get; init; }
    public DeployInfo? LastDeploy { get; init; }
    public int? OpenSecurityAlerts { get; init; }
    public string? Region { get; init; }
    /// <summary>Custom domains the site answers on — the RDAP expiry lookup's input. Not carried onto the report.</summary>
    public List<string> CustomHosts { get; init; } = new();
    public ContentCheckInfo? ContentCheck { get; init; }
    public PackageHealthInfo? Packages { get; init; }

    public WebService ToWebService() => new()
    {
        Name = Name,
        FriendlyName = FriendlyName,
        ResourceGroup = ResourceGroup,
        ResourceType = ResourceTypeRaw,
        Kind = Kind,
        Url = Url ?? "",
        HttpStatus = HttpStatus,
        PlatformState = PlatformState,
        Connectivity = Connectivity,
        Metrics7Days = Metrics7Days,
        FreeTierCheck = FreeTierCheck,
        AppServicePlan = AppServicePlan,
        AppServicePlanSku = AppServicePlanSku,
        ResourceId = ResourceId,
        LastDeploy = LastDeploy,
        OpenSecurityAlerts = OpenSecurityAlerts,
        Region = Region,
        ContentCheck = ContentCheck,
        Packages = Packages,
    };
}
