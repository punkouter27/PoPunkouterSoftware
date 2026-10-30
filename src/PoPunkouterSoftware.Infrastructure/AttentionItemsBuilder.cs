using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Builds the human-readable "attention items" list from an <see cref="AzureReport"/> — the
/// same list rendered as <c>OpsSummary.AttentionItems</c> in the first-paint contract and fed
/// to <see cref="AiTriageService"/> as the triage model's input. Extracted out of
/// <c>DiagEndpoints.BuildOpsSummary</c> so the API's read-time projection and
/// <c>ReportRefreshRunner</c>'s scan-time AI precompute cannot drift from each other — both
/// now call this one method.
///
/// <para>Lives in Infrastructure (not API) so it can also be exercised from
/// <c>ReportRefreshRunner</c> without a slice-to-slice reference; the caller supplies the
/// name-exclusion predicate (<c>PortfolioIdentity.IsSelf</c> in API) rather than this
/// method depending on it directly, since <c>PortfolioIdentity</c> is internal to API.</para>
/// </summary>
public static class AttentionItemsBuilder
{
    /// <summary>Days before a domain's registration lapses at which it becomes an attention item and a push.</summary>
    public const int DomainAlertDays = 30;

    /// <summary>
    /// The exclusion-filtered service list plus every count and flag <c>BuildOpsSummary</c>
    /// needs, so callers never have to recompute the filter themselves.
    /// </summary>
    public sealed record Result(
        List<string> AttentionItems,
        List<WebService> Services,
        int Total,
        int Active,
        int Broken,
        int SecurityFindings,
        int CleanupCandidates,
        bool IsStale,
        int AdvisorFindings = 0);

    /// <param name="report">The scan result to summarize.</param>
    /// <param name="isExcluded">
    /// True when a service (by friendly name / resource name) must be excluded from health
    /// and attention accounting — this site itself and retired apps. Takes a
    /// <c>string?[]</c> (not two positional strings) so API's
    /// <c>PortfolioIdentity.IsSelf(params string?[])</c> converts straight to this
    /// delegate as a method group.
    /// </param>
    public static Result Build(AzureReport report, Func<string?[], bool> isExcluded)
    {
        // The dashboard never reports on the site rendering it, nor on retired apps. Both are
        // real resources in the scanned subscription, so they arrive through the inventory
        // path regardless of the catalog — this exclusion must be applied before any count or
        // attention item is derived from the service list.
        var services = (report.WebServices?.Services ?? new List<WebService>())
            .Where(s => !isExcluded([s.FriendlyName, s.Name]))
            .ToList();
        var total = services.Count;
        var active = services.Count(s => ServiceHealth.IsHealthy(s.HttpStatus));
        var broken = services.Count(s => ServiceHealth.IsBroken(s.HttpStatus));
        var cleanup = (report.OrphanedResources?.Count ?? 0)
            + (report.ZombieApps?.Count ?? 0)
            + report.AppServicePlanInventory.Count(p => p.AppCount == 0);
        var insecureStorage = (report.StorageInventory ?? new()).Count(s =>
            s.PublicBlobAccess || !s.HttpsOnly || s.MinTls is "TLS1_0" or "TLS1_1");
        var criticalDrift = (report.ConfigDrift ?? new()).Count(d =>
            d.Issues?.Any(i => SeverityLevel.Rank(i.Severity) <= SeverityLevel.Rank(SeverityLevel.High)) == true);
        var security = insecureStorage + criticalDrift;
        var attention = new List<string>();

        // Resource Health's verdict rides on the sentence, because "is it me or Azure?" is the
        // first question an outage raises and the diagnosis that answers it sits behind
        // Advanced diagnostics.
        var platformCaused = (report.DowntimeDiagnoses ?? [])
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().PlatformCaused, StringComparer.OrdinalIgnoreCase);
        attention.AddRange(services
            .Where(s => !ServiceHealth.IsHealthy(s.HttpStatus))
            .Take(3)
            .Select(s => $"{(string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName)} is unavailable"
                + platformCaused.GetValueOrDefault(s.Name) switch
                {
                    true => " — Azure platform event",
                    false => " — app-side, Azure reports the platform healthy",
                    null => "",
                }));
        // Up but wrong: each of these is a service the probe calls healthy. AzurePriorityQueue
        // ranks the same four facts (DashboardDerivations.BuildPriorityQueue, FromQuality) so
        // nothing vanishes when the queue replaces this list.
        static string NameOf(WebService s) => string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName;
        attention.AddRange(services
            .Where(s => s.ContentCheck is { Found: false, Error: null })
            .Select(s => $"{NameOf(s)} answers, but its page no longer contains \"{s.ContentCheck!.Expect}\""));
        attention.AddRange(services
            .Where(s => s.LastDeploy?.Conclusion == "failure")
            .Select(s => $"{NameOf(s)}'s latest build failed"));
        attention.AddRange(services
            .Where(s => s.Packages is { Vulnerable.Count: > 0 })
            .Select(s => $"{NameOf(s)} pins {s.Packages!.Vulnerable.Count} package{(s.Packages.Vulnerable.Count == 1 ? "" : "s")} with a known vulnerability"));
        attention.AddRange(report.DomainExpiries
            .Where(d => d.ExpiresAt < DateTime.UtcNow.AddDays(DomainAlertDays))
            .Select(d => $"Domain {d.Domain} expires in {Math.Max(0, (int)(d.ExpiresAt - DateTime.UtcNow).TotalDays)} days"));
        if (security > 0)
            attention.Add($"{security} security configuration finding{(security == 1 ? "" : "s")}");
        if (cleanup > 0)
            attention.Add($"{cleanup} cleanup candidate{(cleanup == 1 ? "" : "s")}");
        // High-impact Cost and Security only. Everything else ranks in the priority queue, but
        // live, Advisor raised 62 "high impact" availability tips (zone redundancy, paid tiers)
        // against a free-tier estate — counting those would mean the hero never once says
        // "everything looks good".
        var advisor = report.AdvisorRecommendations.Count(a => a.Impact == "High" && a.Category is "Cost" or "Security");
        if (advisor > 0)
            attention.Add($"{advisor} high-impact Azure Advisor cost/security recommendation{(advisor == 1 ? "" : "s")}");
        var isStale = PortfolioFreshness.IsStale(report.GeneratedAt, DateTime.UtcNow);
        if (isStale)
            attention.Add("Azure data is stale and should be refreshed");

        return new Result(attention, services, total, active, broken, security, cleanup, isStale, advisor);
    }
}
