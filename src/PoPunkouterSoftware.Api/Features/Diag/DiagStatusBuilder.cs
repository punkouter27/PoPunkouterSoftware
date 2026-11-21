using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Api.Platform;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Projects a <see cref="HealthReport"/> onto <see cref="DiagReportDto"/>, the payload of <c>GET /api/diag</c>
/// behind the <c>/diag</c> page (System status).
///
/// <para>The page is anonymous in Production, so this is an ALLOW-list: each row carries the check's name, its
/// status, the latency, and text written here. The check's <c>Data</c> dictionary is never read — it holds
/// exception messages, row counts and scan timestamps — and neither is the masked config block <c>/health</c>
/// shows outside Production. A description that is not a plain verdict word (a check that threw gets the
/// exception MESSAGE as its description) is replaced by the exception TYPE name.</para>
/// </summary>
internal static partial class DiagStatusBuilder
{
    private sealed record Known(string Group, string Label, string Purpose);

    private static readonly Dictionary<string, Known> Checks = new(StringComparer.Ordinal)
    {
        ["TableStorage"] = new("data", "Table Storage", "Keeps each Azure scan and the 30-day history behind the uptime grid."),
        ["AzureInventory"] = new("data", "Azure scan data", "The latest scan of the Azure estate (read through Azure Resource Manager) — what every page is drawn from."),
        ["KeyVault"] = new("service", "Key Vault", "Holds the app's secrets (API keys), read once when the app starts."),
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
    };

    [GeneratedRegex("^[a-z][a-z-]{0,31}$")]
    private static partial Regex VerdictWord();

    internal static DiagReportDto Build(
        HealthReport report, string environment, IReadOnlyList<DiagCheckDto> probes, DateTimeOffset now)
    {
        var checks = new List<DiagCheckDto>
        {
            // The API row: this server answered. The page replaces the latency with its own round trip.
            new("server", "api", "App server", nameof(HealthStatus.Healthy),
                "Serves every page and all the data on them.", "Answered this request.",
                Math.Round(report.TotalDuration.TotalMilliseconds, 1), null),
        };
        // A live probe replaces a health-check entry of the same name (the ApplicationInsights health
        // check only reads configuration; its probe runs a real query).
        var probed = probes.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        checks.AddRange(report.Entries.Where(kvp => !probed.Contains(kvp.Key)).Select(kvp => FromEntry(kvp.Key, kvp.Value)));
        // Every other outside service, each probed live by ExternalServiceProbes.
        checks.AddRange(probes);

        var ordered = checks
            .OrderBy(c => c.Group switch { "api" => 0, "data" => 1, _ => 2 })
            .ToArray();
        var status = ordered.Any(c => c.Status == nameof(HealthStatus.Unhealthy)) ? nameof(HealthStatus.Unhealthy)
            : ordered.Any(c => c.Status == nameof(HealthStatus.Degraded)) ? nameof(HealthStatus.Degraded)
            : nameof(HealthStatus.Healthy);

        return new DiagReportDto(
            status,
            environment,
            PoPlatform.Version,
            now,
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            ordered);
    }

    private static DiagCheckDto FromEntry(string name, HealthReportEntry entry)
    {
        var verdict = entry.Description is { } d && VerdictWord().IsMatch(d)
            ? d
            : entry.Exception?.GetType().Name ?? "error";
        var known = Checks.GetValueOrDefault(name) ?? new Known("service", name, "A dependency of this app.");
        var summary = Summaries.GetValueOrDefault(verdict)
            ?? (entry.Status == HealthStatus.Healthy ? "Working." : $"The check reported a problem ({verdict}).");
        return new DiagCheckDto(
            name, known.Group, known.Label, entry.Status.ToString(), known.Purpose, summary,
            Math.Round(entry.Duration.TotalMilliseconds, 1),
            entry.Status == HealthStatus.Healthy ? null : summary);
    }
}
