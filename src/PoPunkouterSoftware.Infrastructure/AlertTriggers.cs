using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// The scan-to-scan comparisons that decide whether <c>ReportRefreshRunner</c> pushes an
/// alert. Every rule fires on a CROSSING, never on a state: a certificate that is 10 days from
/// expiry tonight was 11 days from it yesterday, and nagging nightly about the same fact is how
/// a notification channel gets muted. Pure and I/O-free.
/// </summary>
public static class AlertTriggers
{
    /// <summary>Days of certificate life left at which an expiry becomes worth a push.</summary>
    public const int SslAlertDays = 14;

    /// <summary>
    /// Certificates that are inside <see cref="SslAlertDays"/> now and were not in the previous
    /// report — either further out, or absent (a newly added host already close to expiry).
    /// </summary>
    public static List<SslEntry> NewSslExpiries(AzureReport current, AzureReport? previous)
    {
        var before = (previous?.SslExpiry ?? [])
            .Where(s => s.DaysLeft is <= SslAlertDays)
            .Select(s => s.Url ?? s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (current.SslExpiry ?? [])
            .Where(s => s.DaysLeft is <= SslAlertDays && !before.Contains(s.Url ?? s.Name))
            .OrderBy(s => s.DaysLeft)
            .ToList();
    }

    /// <summary>
    /// The current scan's forecast when it has just crossed into "over budget"; null when it
    /// has not, when the previous scan was already over, or when no budget is set.
    /// </summary>
    public static CostForecast? BudgetCrossed(AzureReport current, AzureReport? previous, double? budgetUsd)
    {
        var now = DashboardInsightsBuilder.BuildForecast(current, [], budgetUsd);
        if (now.Verdict != CostVerdict.Over)
            return null;
        return previous is not null && DashboardInsightsBuilder.BuildForecast(previous, [], budgetUsd).Verdict == CostVerdict.Over
            ? null
            : now;
    }

    /// <summary>
    /// Services whose page answered without the catalog's expected text now, and did not at
    /// the previous scan (it was found, or the check could not read the page).
    /// </summary>
    public static List<WebService> NewContentFailures(AzureReport current, AzureReport? previous)
    {
        static bool Failed(WebService s) => s.ContentCheck is { Found: false, Error: null };
        var before = (previous?.WebServices?.Services ?? []).Where(Failed)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (current.WebServices?.Services ?? []).Where(s => Failed(s) && !before.Contains(s.Name)).ToList();
    }

    /// <summary>Domains inside <see cref="AttentionItemsBuilder.DomainAlertDays"/> now that were not at the previous scan.</summary>
    public static List<DomainExpiry> NewDomainExpiries(AzureReport current, AzureReport? previous, DateTime now)
    {
        var line = now.AddDays(AttentionItemsBuilder.DomainAlertDays);
        var before = (previous?.DomainExpiries ?? []).Where(d => d.ExpiresAt < line)
            .Select(d => d.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current.DomainExpiries.Where(d => d.ExpiresAt < line && !before.Contains(d.Domain)).ToList();
    }
}
