namespace PoPunkouterSoftware.Shared;

// The root report envelope: what one Azure scan produced, plus scan-level metadata (per-step timings and the run-over-run delta).
// GoF: Value Object - all records are immutable data carriers with no behaviour.

public record AzureReport
{
    public DateTime? GeneratedAt { get; init; }
    public SubscriptionInfo? Subscription { get; init; }
    public WebServicesInfo? WebServices { get; init; }
    public CostInfo? Cost { get; init; }
    public AllResourceSummaryInfo? AllResourceSummary { get; init; }
    public List<SslEntry>? SslExpiry { get; init; }
    public List<ConfigDriftItem>? ConfigDrift { get; init; }
    public List<StorageItem>? StorageInventory { get; init; }
    public List<AiServiceInventoryItem> AiServicesInventory { get; init; } = new();
    public List<LogAnalyticsWorkspaceItem> LogAnalyticsInventory { get; init; } = new();
    public List<AppInsightsMetric>? AppInsightsMetrics { get; init; }
    public List<ZombieApp>? ZombieApps { get; init; }
    public List<OrphanedResource>? OrphanedResources { get; init; }
    public List<CatalogDriftItem>? CatalogDrift { get; init; }
    public BurnRateInfo? BurnRate { get; init; }
    public List<StepTimingEntry>? StepTimings { get; init; }
    public List<AppServicePlanInventoryEntry> AppServicePlanInventory { get; init; } = new();
    public ReportDelta? Delta { get; init; }
    /// <summary>Root-cause analysis for each broken or unreachable App Service.</summary>
    public List<ServiceDowntimeDiagnosis>? DowntimeDiagnoses { get; init; }
    /// <summary>
    /// Precomputed AI (or rule-based fallback) triage paragraph for this scan, attached by
    /// <c>ReportRefreshRunner</c> before the report is saved. Null only for reports persisted
    /// before this field existed.
    /// </summary>
    public AiSummaryResult? AiSummary { get; init; }
    /// <summary>Azure Advisor's open recommendations. Empty when Advisor has none or did not answer.</summary>
    public List<AdvisorRecommendation> AdvisorRecommendations { get; init; } = new();
    /// <summary>Exceptions per app over the 24 hours before the scan, from the shared Application Insights.</summary>
    public List<AppErrorSummary> AppErrors { get; init; } = new();
    /// <summary>App Service plan prices from the Azure Retail Prices API, per region and OS.</summary>
    public List<TierPrice> TierPrices { get; init; } = new();
    /// <summary>Registration expiry of every custom domain the scanned apps answer on (RDAP).</summary>
    public List<DomainExpiry> DomainExpiries { get; init; } = new();
}

/// <summary>One App Service plan SKU's pay-as-you-go price, per month (730 hours).</summary>
public record TierPrice
{
    public string Region { get; init; } = "";
    public bool Linux { get; init; }
    public string Sku { get; init; } = "";
    public double MonthlyUsd { get; init; }
}

/// <summary>A registered domain's expiry date, read from its registry over RDAP.</summary>
public record DomainExpiry
{
    public string Domain { get; init; } = "";
    public DateTime ExpiresAt { get; init; }
}

/// <summary>One exception type an app threw in the 24 hours before the scan.</summary>
public record AppErrorType
{
    public string Type { get; init; } = "";
    public int Count { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
}

/// <summary>One app's exception count over the last 24 hours, and its most frequent exception TYPE.</summary>
public record AppErrorSummary
{
    /// <summary>cloud_RoleName as the app reports it to Application Insights.</summary>
    public string App { get; init; } = "";
    public int Count24h { get; init; }
    /// <summary>
    /// The exception type only, never the message: messages can carry user data, and this
    /// reaches the anonymous first-paint contract.
    /// </summary>
    public string? TopType { get; init; }
    /// <summary>Up to ten exception types, most frequent first, with when each was first and last seen.</summary>
    public List<AppErrorType> Types { get; init; } = new();
}

public record SubscriptionInfo { public string Name { get; init; } = ""; }

public record StepTimingEntry
{
    public string Step { get; init; } = "";
    public long ElapsedMs { get; init; }
}

// ─── Item 1: Report Delta / Trending ──────────────────────────────────────────

public record ReportDelta
{
    public DateTime? PreviousGeneratedAt { get; init; }
    public int? BrokenServicesDelta { get; init; }
    public double? CostDelta { get; init; }
    public List<string> NewBrokenServices { get; init; } = new();
    public List<string> RecoveredServices { get; init; } = new();
    public List<string> NewOrphanedResources { get; init; } = new();
}

// ─── CI/CD Infrastructure Review models ──────────────────────────────────────
