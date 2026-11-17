using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Projects a <see cref="HealthReport"/> onto <see cref="DiagStatus"/>, the payload behind the
/// friendly <c>/diag</c> page (owner's call, 2026-11-17, reversing the 2026-09-04 removal).
///
/// <para>The page is anonymous in Production, so this is an ALLOW-list: each entry carries the
/// check's name, its status, its one-word verdict, the latency, and text written here. The
/// check's <c>Data</c> dictionary is never read — it holds exception messages, row counts and
/// scan timestamps — and neither is the masked config block <c>/health</c> shows outside
/// Production. A description that is not a plain verdict word (a check that threw gets the
/// exception MESSAGE as its description) is replaced by the exception TYPE name, which is the
/// most the diag standard allows.</para>
/// </summary>
internal static partial class DiagStatusBuilder
{
    private sealed record Known(string Category, string Purpose);

    private static readonly Dictionary<string, Known> Checks = new(StringComparer.Ordinal)
    {
        ["TableStorage"] = new("storage", "Keeps each Azure scan and the 30-day history behind the uptime grid."),
        ["AzureInventory"] = new("storage", "The latest scan of the Azure estate — what every page is drawn from."),
        ["KeyVault"] = new("service", "Holds the app's secrets (API keys), read once when the app starts."),
        ["ApplicationInsights"] = new("service", "Telemetry for this app, and the sign-in data behind the Users page."),
    };

    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["readable"] = "Working — the app read from it just now.",
        ["populated"] = "A scan is stored and has data in it.",
        ["configured"] = "Set up on this server.",
        ["not-configured"] = "Not set up on this server — the app works without it.",
        ["forbidden"] = "Reachable, but the app's identity is not allowed to read it.",
        ["no-credential"] = "No Azure sign-in is available to reach it.",
        ["unreachable"] = "Could not be reached.",
        ["unavailable"] = "Not available right now — the app is serving from its local copy.",
        ["no-report"] = "No scan has been stored yet.",
        ["empty-inventory"] = "The last scan finished but found nothing.",
        ["disabled"] = "Turned off on this server.",
        ["enabled"] = "Turned on. Not probed here: every call is a paid completion.",
    };

    [GeneratedRegex("^[a-z][a-z-]{0,31}$")]
    private static partial Regex VerdictWord();

    internal static DiagStatus Build(HealthReport report, string environment, AiTriageService ai, DateTimeOffset now)
    {
        var checks = report.Entries
            .Select(kvp => FromEntry(kvp.Key, kvp.Value))
            .ToList();

        // The AI provider is the one external dependency with no health check, on purpose:
        // probing it costs money on every page load. Its row reports configuration only.
        var aiVerdict = ai.IsEnabled ? "enabled" : "disabled";
        checks.Add(new DiagCheck(
            "AI summaries", "service", nameof(HealthStatus.Healthy), aiVerdict, Summaries[aiVerdict],
            "Writes the plain-English status paragraph on the Azure page.", LatencyMs: null));

        return new DiagStatus(
            report.Status.ToString(),
            environment,
            now,
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks
                .OrderBy(c => c.Category == "storage" ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList());
    }

    private static DiagCheck FromEntry(string name, HealthReportEntry entry)
    {
        var verdict = entry.Description is { } d && VerdictWord().IsMatch(d)
            ? d
            : entry.Exception?.GetType().Name ?? "error";
        var known = Checks.GetValueOrDefault(name) ?? new Known("service", "A dependency of this app.");
        var summary = Summaries.GetValueOrDefault(verdict)
            ?? (entry.Status == HealthStatus.Healthy ? "Working." : "The check reported a problem.");
        return new DiagCheck(
            name, known.Category, entry.Status.ToString(), verdict, summary, known.Purpose,
            Math.Round(entry.Duration.TotalMilliseconds, 1));
    }
}
