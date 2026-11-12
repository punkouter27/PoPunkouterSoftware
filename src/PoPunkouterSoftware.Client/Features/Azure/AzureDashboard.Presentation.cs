using Radzen;

namespace PoPunkouterSoftware.Client;

// Partial class — display-only mapping: raw domain strings to the labels, badge styles
// and CSS classes the markup renders. Nothing here reads or mutates page state, so a
// visual tweak never risks touching behaviour.
//
// (Named .Charts.cs until 2026-07, by which point it contained no chart code at all —
// every chart-data property in it had been orphaned by the compact-dashboard redesign.)
public partial class AzureDashboard
{
    private static BadgeStyle ActionabilityBadge(string? tier) => tier switch
    {
        "Fix Now" => BadgeStyle.Danger,
        "Fix Soon" => BadgeStyle.Warning,
        "Remove Candidate" => BadgeStyle.Secondary,
        _ => BadgeStyle.Info,
    };

    private static BadgeStyle ExplorerStatusBadge(string status) => status switch
    {
        "active" or "healthy" => BadgeStyle.Success,
        "broken" => BadgeStyle.Danger,
        "unreachable" or "review" => BadgeStyle.Warning,
        _ => BadgeStyle.Info,
    };

    private static string RiskClass(string risk) => risk switch
    {
        var value when value.Contains("Security", StringComparison.OrdinalIgnoreCase) => "danger",
        var value when value.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) => "danger",
        var value when value.Contains("Waste", StringComparison.OrdinalIgnoreCase) => "warning",
        var value when value.Contains("Drift", StringComparison.OrdinalIgnoreCase) => "warning",
        _ => "muted",
    };
}
