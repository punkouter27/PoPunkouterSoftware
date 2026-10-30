namespace PoPunkouterSoftware.Shared;

// The web-service fleet: discovery, reachability, 7-day metrics and free-tier state.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

public record WebServicesInfo
{
    public int Total { get; init; }
    public ByStatusInfo? ByStatus { get; init; }
    public List<WebService> Services { get; init; } = new();
}

public record ByStatusInfo { public int Active { get; init; } public int Broken { get; init; } public int Other { get; init; } }

public record WebService
{
    public string Name { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string? Kind { get; init; }
    public string Url { get; init; } = "";
    public string HttpStatus { get; init; } = "";
    public string? PlatformState { get; init; }
    public string? Description { get; init; }
    public string? ResourceId { get; init; }
    /// <summary>App Service Plan name — populated if service is a Microsoft.Web/sites resource.</summary>
    public string? AppServicePlan { get; init; }
    /// <summary>App Service Plan SKU — e.g. F1, B2, S1. Resolved from the serverFarm resource.</summary>
    public string? AppServicePlanSku { get; init; }
    public ConnectivityInfo? Connectivity { get; init; }
    public MetricsInfo? Metrics7Days { get; init; }
    public FreeTierCheckInfo? FreeTierCheck { get; init; }
    /// <summary>
    /// The last completed push-triggered GitHub Actions run on the repo matched to this service
    /// — "deploy" in practice, since every Po repo deploys on push to its default branch. Null
    /// with no PAT configured or no matching repo.
    /// </summary>
    public DeployInfo? LastDeploy { get; init; }
    /// <summary>
    /// Open Dependabot alerts on the matched repo (capped at 100). Null when there is no
    /// matched repo or the PAT cannot read its alerts — unknown, not clean.
    /// </summary>
    public int? OpenSecurityAlerts { get; init; }
    /// <summary>Azure region name as ARM reports it ("westus2"). Prices the tier options.</summary>
    public string? Region { get; init; }
    /// <summary>
    /// The catalog's <c>expect</c> text checked against the live page. Null when the catalog
    /// sets no expectation for this app.
    /// </summary>
    public ContentCheckInfo? ContentCheck { get; init; }
    /// <summary>
    /// The matched repo's central NuGet versions checked against nuget.org and OSV.dev. Null
    /// when there is no matched repo or it has no <c>Directory.Packages.props</c>.
    /// </summary>
    public PackageHealthInfo? Packages { get; init; }
}

/// <summary>Whether the live page still contains the text the catalog says it must.</summary>
public record ContentCheckInfo
{
    public string Expect { get; init; } = "";
    public bool Found { get; init; }
    /// <summary>Why the page could not be read at all — a status code or a timeout.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Package freshness for one repo. Lists are "Name version" strings, capped, so the scan
/// stays small enough to ride the first-paint contract.
/// </summary>
public record PackageHealthInfo
{
    /// <summary>Packages read from the manifest.</summary>
    public int Checked { get; init; }
    /// <summary>Packages with a newer stable release on nuget.org ("Name 1.2.0 → 1.3.0").</summary>
    public List<string> Outdated { get; init; } = new();
    /// <summary>Packages whose pinned version has a known advisory on OSV.dev ("Name 1.2.0 (GHSA-…)").</summary>
    public List<string> Vulnerable { get; init; } = new();
}

public record DeployInfo
{
    public DateTime CompletedAt { get; init; }
    /// <summary>success | failure | cancelled — GitHub's run conclusion.</summary>
    public string? Conclusion { get; init; }
    /// <summary>Full commit SHA the run built.</summary>
    public string? Sha { get; init; }
    public string? Url { get; init; }
}

public record ConnectivityInfo
{
    public bool Success { get; init; }
    public int ResponseTime { get; init; }
    public string? Error { get; init; }
    public bool? IsAzureErrorPage { get; init; }
}

public record MetricsInfo { public int Requests { get; init; } public int Http5xx { get; init; } public double AverageResponseTime { get; init; } }

public record FreeTierCheckInfo { public bool IsOnFreeTier { get; init; } public bool IsOnPaidTier { get; init; } public bool CanGoFree { get; init; } }
