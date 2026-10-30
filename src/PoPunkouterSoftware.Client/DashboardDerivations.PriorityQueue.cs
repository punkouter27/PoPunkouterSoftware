using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// The ranked "Needs attention" queue: every finding in the report — reliability, cost,
/// security, cleanup, Advisor and the "up but wrong" quality checks — as one list ordered by
/// actionability tier, then impact. It replaces the first-paint attention sentences in place,
/// so a fact that reaches those sentences must reach this list too.
/// </summary>
public static partial class DashboardDerivations
{
    public static List<PriorityQueueItem> BuildPriorityQueue(
        AzureReport? r,
        List<ConsolidatedService> consolidated,
        List<SafeToRemoveItem> safe)
    {
        // One pass over six source sequences, each contributing its own tier of findings.
        // The previous shape was six independent `foreach` loops that all appended into the
        // same `List<PriorityQueueItem>` and then a single GroupBy/OrderBy pass — six
        // enumerations, six temporary lists, and a dedup keyed on `{Source}|{Item}` that
        // never matched across source types anyway (a "Reliability|app-x" item can never
        // collide with a "Security|app-x" item). The new shape is one SelectMany and the
        // pipeline at the bottom is the only place a sort or dedup lives.
        IEnumerable<PriorityQueueItem> FromReliability() =>
            consolidated
                .Where(x => x.Actionability is "Fix Now" or "Fix Soon")
                .Select(c => new PriorityQueueItem(
                    Actionability: c.Actionability,
                    Item: c.DisplayName,
                    Source: "Reliability",
                    ImpactScore: 100 - c.HealthScore,
                    Confidence: c.HealthScore < 50 ? "high" : "medium",
                    Reason: $"Status={c.HttpStatus}; 7d 5xx={c.Http5xx7d}; Reliability={c.ReliabilityScore}%",
                    Owner: c.Owner,
                    Environment: c.Environment,
                    Command: c.Command));

        IEnumerable<PriorityQueueItem> FromSafeToRemove() =>
            safe.Select(s => new PriorityQueueItem(
                Actionability: "Remove Candidate",
                Item: s.Name,
                Source: "SafeToRemove",
                ImpactScore: s.Confidence == "high" ? 75 : 55,
                Confidence: s.Confidence,
                Reason: s.Reason,
                Owner: "unassigned",
                Environment: "unknown",
                Command: s.Command));

        IEnumerable<PriorityQueueItem> FromSsl() =>
            (r?.SslExpiry ?? new List<SslEntry>())
                .Where(x => x.DaysLeft is < 60)
                .Take(20)
                .Select(ssl =>
                {
                    var days = ssl.DaysLeft ?? 0;
                    return new PriorityQueueItem(
                        Actionability: days < 14 ? "Fix Now" : "Fix Soon",
                        Item: ssl.Name,
                        Source: "SSL",
                        ImpactScore: days < 14 ? 90 : 65,
                        Confidence: "high",
                        Reason: $"Certificate expires in {days} days",
                        Owner: "unassigned",
                        Environment: InferEnvironment(ssl.Name, ssl.Name),
                        Command: null);
                });

        IEnumerable<PriorityQueueItem> FromOrphans() =>
            (r?.OrphanedResources ?? new List<OrphanedResource>())
                .Select(o => new PriorityQueueItem(
                    Actionability: "Remove Candidate",
                    Item: o.Name,
                    Source: "Orphaned",
                    ImpactScore: 60,
                    Confidence: "medium",
                    Reason: o.Reason,
                    Owner: "unassigned",
                    Environment: InferEnvironment(o.ResourceGroup, o.Name),
                    Command: o.Command));

        IEnumerable<PriorityQueueItem> FromStorage() =>
            (r?.StorageInventory ?? new List<StorageItem>())
                .Where(s => s.IssueCount > 0)
                .Select(storage =>
                {
                    var severe = storage.PublicBlobAccess || !storage.HttpsOnly;
                    return new PriorityQueueItem(
                        Actionability: severe ? "Fix Now" : "Fix Soon",
                        Item: storage.Name,
                        Source: "Security",
                        ImpactScore: severe ? 95 : 72,
                        Confidence: "high",
                        Reason: string.Join("; ", storage.Issues?.Select(i => i.Issue) ?? []),
                        Owner: InferOwner(storage.ResourceGroup, storage.Name),
                        Environment: InferEnvironment(storage.ResourceGroup, storage.Name),
                        Command: null);
                });

        IEnumerable<PriorityQueueItem> FromDrift() =>
            (r?.ConfigDrift ?? new List<ConfigDriftItem>())
                .Where(d => d.Issues?.Any(i => i.Severity is "critical" or "high") == true)
                .Select(drift => new PriorityQueueItem(
                    Actionability: "Fix Now",
                    Item: drift.FriendlyName ?? drift.Name,
                    Source: "Configuration",
                    ImpactScore: 88,
                    Confidence: "high",
                    Reason: string.Join("; ", drift.Issues?.Where(i => i.Severity is "critical" or "high").Select(i => i.Issue) ?? []),
                    Owner: InferOwner(drift.ResourceGroup, drift.Name),
                    Environment: InferEnvironment(drift.ResourceGroup, drift.Name),
                    Command: null));

        // Azure Advisor, ranked into the same queue rather than shown as a list of its own —
        // one row per RECOMMENDATION, not per resource. Live, Advisor repeats the same five
        // availability tips across every free-tier app and storage account (75 rows); grouped,
        // that is ~10. Only Cost and
        // Security at High impact earn "Fix Soon": "add zone redundancy" to a hobby estate is
        // worth knowing, not worth fixing now. Low impact is left out entirely.
        IEnumerable<PriorityQueueItem> FromAdvisor() =>
            (r?.AdvisorRecommendations ?? [])
                .Where(a => a.Impact is "High" or "Medium")
                .GroupBy(a => (a.Category, a.Problem))
                .Select(g =>
                {
                    var high = g.Any(a => a.Impact == "High");
                    var urgent = high && g.Key.Category is "Cost" or "Security";
                    var names = g.Select(a => a.ImpactedName).Where(n => n.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var savings = g.Sum(a => a.MonthlySavings ?? 0);
                    return new PriorityQueueItem(
                        Actionability: urgent ? "Fix Soon" : "Watch",
                        Item: g.Key.Problem,
                        Source: $"Advisor · {g.Key.Category}",
                        ImpactScore: (high ? 70 : 40) + Math.Min(20, names.Count),
                        Confidence: "high",
                        Reason: $"{Plural(names.Count, "resource")}: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? "…" : "")}"
                            + (savings > 0 ? $" · saves ~${savings:F2}/mo" : ""),
                        Owner: "unassigned",
                        Environment: "unknown",
                        Command: null);
                });

        // Up but wrong — the four facts AttentionItemsBuilder states as sentences on first
        // paint, so they survive this queue replacing that list.
        IEnumerable<PriorityQueueItem> FromQuality()
        {
            foreach (var s in r?.WebServices?.Services ?? [])
            {
                var name = string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName;
                PriorityQueueItem Row(string tier, string source, int impact, string reason) => new(
                    Actionability: tier, Item: name, Source: source, ImpactScore: impact, Confidence: "high",
                    Reason: reason, Owner: InferOwner(s.ResourceGroup, s.Name),
                    Environment: InferEnvironment(s.ResourceGroup, s.Name), Command: null);

                if (s.ContentCheck is { Found: false, Error: null } check)
                    yield return Row("Fix Now", "Content", 85, $"Page answers but no longer contains \"{check.Expect}\"");
                if (s.LastDeploy?.Conclusion == "failure")
                    yield return Row("Fix Soon", "Build", 70, $"Latest build failed {s.LastDeploy.CompletedAt:MMM d}");
                if (s.Packages is { Vulnerable.Count: > 0 } packages)
                    yield return Row("Fix Soon", "Packages", 75, string.Join("; ", packages.Vulnerable.Take(3)));
            }
            foreach (var d in r?.DomainExpiries ?? [])
            {
                var days = (int)(d.ExpiresAt - DateTime.UtcNow).TotalDays;
                if (days < 60)
                    yield return new PriorityQueueItem(
                        Actionability: days < 30 ? "Fix Now" : "Fix Soon", Item: d.Domain, Source: "Domain",
                        ImpactScore: days < 30 ? 92 : 65, Confidence: "high",
                        Reason: $"Registration expires {d.ExpiresAt:MMM d, yyyy}", Owner: "unassigned",
                        Environment: "production", Command: null);
            }
        }

        return Enumerable.Empty<PriorityQueueItem>()
            .Concat(FromReliability())
            .Concat(FromQuality())
            .Concat(FromAdvisor())
            .Concat(FromSafeToRemove())
            .Concat(FromSsl())
            .Concat(FromOrphans())
            .Concat(FromStorage())
            .Concat(FromDrift())
            // Within a source, the same item can appear twice — most realistically two SSL
            // entries for the same hostname, one near expiry and one far. Keeping the
            // higher-impact one matches the badge the queue already showed and stops two
            // rows competing for the same line. Across sources dedup never fired anyway
            // (Reliability|app-x and Security|app-x are distinct keys), so this is a narrow,
            // intentional dedup rather than the broad "same finding everywhere" shape the
            // previous GroupBy looked like it was doing.
            .GroupBy(i => $"{i.Source}|{i.Item}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.ImpactScore).First())
            // Tier first, magnitude second. Ordering by ImpactScore alone put a "Fix Soon"
            // scoring 72 above a "Fix Now" scoring 50, so a queue whose whole promise is
            // "most urgent first" opened with the wrong badge and alternated the rest of the
            // way down — the reader has to choose between believing the badge or the order.
            // The badge is the claim; the order now agrees with it, and impact ranks WITHIN
            // a tier, which is the only comparison the score is meaningful for anyway.
            // The 150 cap was a future-proof against an estate this size has never reached;
            // the live queue is < 30 items, so removing it costs nothing.
            .OrderBy(i => ActionabilityRank(i.Actionability))
            .ThenByDescending(i => i.ImpactScore)
            .ThenBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
