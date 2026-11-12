namespace PoPunkouterSoftware.Client;

/// <summary>
/// One logical application, rolled up from every Azure resource that belongs to it — an app
/// with a Web App and a Static Web App is one row here, not two.
///
/// <para>A top-level record rather than a nested one because it now crosses a class boundary:
/// <see cref="DashboardDerivations.BuildConsolidatedServices"/> produces it and both
/// <see cref="DashboardDerivations.BuildPriorityQueue"/> and
/// <see cref="DashboardDerivations.BuildResourceExplorerItems"/> consume it. Never serialized
/// over the wire — this is a client-side projection, not a DTO.</para>
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public sealed record ConsolidatedService(
    string Key,
    string DisplayName,
    string ResourceTypeSummary,
    string HttpStatus,
    int Requests7d,
    int Http5xx7d,
    int? ResponseTimeMs,
    int HealthScore,
    int ReliabilityScore,
    string Actionability,
    string Owner,
    string Environment,
    string? ResourceGroup,
    string? Command
);
