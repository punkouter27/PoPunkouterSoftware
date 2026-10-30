using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Projects a full report down to its chart-sized <see cref="HistorySummary"/>. Lives in
/// Infrastructure (not Shared) so the contract assembly stays a pure data carrier.
/// Persisted per scan so the history endpoints never have to decompress and deserialize
/// full report blobs again.
/// </summary>
public static class HistorySummaryMapper
{
    public static HistorySummary FromReport(AzureReport r) => new()
    {
        GeneratedAt = r.GeneratedAt ?? DateTime.MinValue,
        TotalServices = r.WebServices?.Total ?? 0,
        ActiveServices = r.WebServices?.ByStatus?.Active ?? 0,
        BrokenServices = r.WebServices?.ByStatus?.Broken ?? 0,
        TotalCost30Days = r.Cost?.TotalCost30Days ?? 0,
        ProjectedMonthCost = r.BurnRate?.ProjectedMonthTotal ?? 0,
        AvgResponseTimeMs = r.WebServices?.Services?.Where(s => s.Connectivity?.Success == true)
            .Select(s => (double)(s.Connectivity?.ResponseTime ?? 0))
            .DefaultIfEmpty(0).Average() ?? 0,
        Total5xxErrors = r.WebServices?.Services?.Sum(s => s.Metrics7Days?.Http5xx ?? 0) ?? 0,
        TotalResources = r.AllResourceSummary?.Total ?? 0,
        ScanDurationMs = r.StepTimings?.Sum(t => t.ElapsedMs) ?? 0,
        BrokenDelta = r.Delta?.BrokenServicesDelta,
        Services = (r.WebServices?.Services ?? new()).Select(s => new ServiceHistoryPoint
        {
            // Shared with ServicePingerService via NameMatching.ServiceIdentity: the uptime
            // grid joins scan rows and ping rows on this exact string.
            Name = NameMatching.ServiceIdentity(s.FriendlyName, s.Name),
            HttpStatus = s.HttpStatus,
            ResponseTimeMs = s.Connectivity?.ResponseTime ?? 0,
            Requests7d = s.Metrics7Days?.Requests ?? 0,
            LastDeployAt = s.LastDeploy?.CompletedAt,
        }).ToList(),
    };
}
