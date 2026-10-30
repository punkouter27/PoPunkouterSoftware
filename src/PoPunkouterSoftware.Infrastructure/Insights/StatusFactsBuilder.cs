using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Assembles the <see cref="StatusFacts"/> fed to <see cref="AiTriageService"/> — every number
/// the narrator is allowed to use, gathered from one report plus its history window.
///
/// <para>Extracted so the scan-time precompute (<c>ReportRefreshRunner</c>) and any read-time
/// regeneration build the identical fact set, for the same reason
/// <see cref="AttentionItemsBuilder"/> exists: two call sites that assemble the model's input
/// separately will drift, and the drift is invisible until the paragraph starts contradicting
/// the tiles beside it.</para>
///
/// <para>Pure and I/O-free — covered by the Unit tier.</para>
/// </summary>
public static class StatusFactsBuilder
{
    public static StatusFacts Build(
        AzureReport report,
        IReadOnlyCollection<HistorySummary> history,
        double? budgetUsd,
        Func<string?[], bool> isExcluded)
    {
        var built = AttentionItemsBuilder.Build(report, isExcluded);
        var forecast = DashboardInsightsBuilder.BuildForecast(report, history, budgetUsd);
        var delta = DashboardInsightsBuilder.BuildDelta(report, history, isExcluded);

        var brokenNames = built.Services
            .Where(s => !ServiceHealth.IsHealthy(s.HttpStatus))
            .Select(s => string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var avgResponse = built.Services
            .Where(s => s.Connectivity?.Success == true)
            .Select(s => (double)(s.Connectivity?.ResponseTime ?? 0))
            .DefaultIfEmpty(0)
            .Average();

        return new StatusFacts
        {
            SubscriptionName = report.Subscription?.Name ?? "Azure",
            GeneratedAt = report.GeneratedAt,
            IsStale = built.IsStale,
            TotalServices = built.Total,
            ActiveServices = built.Active,
            BrokenServices = built.Broken,
            HealthPercent = built.Total > 0 ? (int)Math.Round(built.Active * 100d / built.Total) : 100,
            TotalResources = report.AllResourceSummary?.Total ?? 0,
            Cost30Days = Math.Round(report.Cost?.TotalCost30Days ?? 0, 2),
            ProjectedMonthCost = forecast.ProjectedMonthTotal,
            BudgetUsd = forecast.BudgetUsd,
            SecurityFindings = built.SecurityFindings,
            CleanupCandidates = built.CleanupCandidates,
            AvgResponseTimeMs = Math.Round(avgResponse, 0),
            BrokenServiceNames = brokenNames,
            AttentionItems = built.AttentionItems,
            // Only the sentence, not the supporting numbers — the model already has the raw
            // figures above and repeating them here just spends tokens.
            RecentChanges = delta.Changes.Select(c => c.Text).Take(6).ToList(),
            TopCostDrivers = (report.Cost?.TopCostDrivers ?? new())
                .Where(d => d.Cost > 0)
                .Take(3)
                .Select(d => $"{d.Name} ${d.Cost:F2}")
                .ToList(),
            SlowestEndpoints = built.Services
                .Where(s => s.Connectivity?.ResponseTime > 0)
                .OrderByDescending(s => s.Connectivity!.ResponseTime)
                .Take(3)
                .Select(s => $"{(string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName)} {s.Connectivity!.ResponseTime} ms")
                .ToList(),
        };
    }
}
