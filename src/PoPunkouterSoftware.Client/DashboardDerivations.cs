using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Every projection the Azure dashboard derives from a raw <see cref="AzureReport"/>: the
/// cleanup candidates, the per-app rollup, the ranked priority queue (in
/// <c>DashboardDerivations.PriorityQueue.cs</c>) and the resource
/// explorer rows.
///
/// <para>These lived as <c>private static</c> members of the <c>AzureDashboard</c> partial
/// class. That made roughly 460 lines of domain logic — impact scoring, actionability tiers,
/// owner and environment inference, safe-to-remove reasoning — unreachable from a test even
/// though both test projects already reference this assembly, so the only client-side code
/// with a unit test was <c>RelativeTime</c>. Nothing about any of it needs a renderer: the
/// methods are pure functions of a report. Lifting them into their own public static class is
/// what makes <c>DashboardDerivationsTests</c> possible.</para>
///
/// <para>Pure by contract. No instance state, no I/O, no <c>DateTime.UtcNow</c> — a caller
/// that needs "now" passes it. Keep it that way; a clock or an HttpClient in here puts the
/// tests back where they started.</para>
/// </summary>
public static partial class DashboardDerivations
{
    /// <summary>Last segment of an ARM resource type to a human label ("sites" -> "App Services").</summary>
    public static string HumanizeResourceType(string type) => type switch
    {
        "storageAccounts" => "Storage Accounts",
        "sites" => "App Services",
        "userAssignedIdentities" => "Managed Identities",
        "workspaces" => "Log Analytics",
        "vaults" => "Key Vaults",
        "components" => "App Insights",
        "serverFarms" => "App Service Plans",
        "accounts" => "Cognitive Services",
        "servers" => "SQL Servers",
        "databases" => "SQL Databases",
        _ => type,
    };

    // ── Label / badge helpers ─────────────────────────────────────────────────
    public static string TypeLabel(string? t) => t switch
    {
        "Microsoft.Web/sites" => "App Service",
        "Microsoft.App/containerApps" => "Container App",
        "Microsoft.Web/staticSites" => "Static Web App",
        _ => t?.Split('/').LastOrDefault() ?? "—",
    };

    // ── Safe-to-remove analysis ───────────────────────────────────────────────
    public static List<SafeToRemoveItem> BuildSafeToRemove(AzureReport? r)
    {
        if (r == null)
            return new();
        var items = new List<SafeToRemoveItem>();
        foreach (var svc in r.WebServices?.Services ?? new())
        {
            var zero = svc.Metrics7Days?.Requests == 0;
            var broken = svc.HttpStatus == "broken";
            var unreachable = svc.HttpStatus == "unreachable";
            var stopped = svc.PlatformState == "Stopped";
            var azErr = svc.Connectivity?.IsAzureErrorPage == true;
            if ((broken || unreachable || stopped || azErr) && zero)
            {
                var reasons = new List<string>();
                if (broken)
                    reasons.Add("HTTP broken");
                if (unreachable)
                    reasons.Add("Unreachable (timeout)");
                if (stopped)
                    reasons.Add("Platform Stopped");
                if (azErr)
                    reasons.Add("Serving Azure error page");
                if (zero)
                    reasons.Add("0 requests in 7 days");
                items.Add(new SafeToRemoveItem
                {
                    Name = svc.Name,
                    ResourceGroup = svc.ResourceGroup,
                    Type = TypeLabel(svc.ResourceType),
                    Source = "Connectivity + Metrics",
                    Reason = string.Join(", ", reasons),
                    Confidence = broken ? "high" : "medium",
                    Command = svc.ResourceType == "Microsoft.Web/sites"
                        ? $"az webapp delete --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\""
                        : null,
                });
            }
        }

        foreach (var orphan in r.OrphanedResources ?? new())
        {
            items.Add(new SafeToRemoveItem
            {
                Name = orphan.Name,
                ResourceGroup = orphan.ResourceGroup,
                Type = orphan.Type,
                Source = "Orphaned resource scan",
                Reason = orphan.Reason,
                Confidence = orphan.EstimatedMonthlyCost?.Contains("Paid", StringComparison.OrdinalIgnoreCase) == true ? "high" : "medium",
                EstimatedMonthlyCost = orphan.EstimatedMonthlyCost,
                Command = orphan.Command,
            });
        }

        foreach (var plan in r.AppServicePlanInventory.Where(p => p.AppCount == 0))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = plan.Name,
                ResourceGroup = plan.ResourceGroup,
                Type = "App Service Plan",
                Source = "Plan inventory",
                Reason = $"No apps assigned (SKU: {plan.Sku ?? "unknown"})",
                Confidence = "high",
                EstimatedMonthlyCost = string.Equals(plan.Sku, "F1", StringComparison.OrdinalIgnoreCase) ? "$0/mo" : "Paid tier",
                Command = $"az appservice plan delete --name \"{plan.Name}\" --resource-group \"{plan.ResourceGroup}\" --yes",
            });
        }

        foreach (var ai in r.AiServicesInventory.Where(a => a.RiskLevel is "cleanup"))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = ai.Name,
                ResourceGroup = ai.ResourceGroup,
                Type = "AI Services",
                Source = "AI inventory",
                Reason = ai.Recommendation,
                Confidence = "medium",
                EstimatedMonthlyCost = ai.Sku is "S0" ? "Usage-based S0" : null,
                Command = $"az cognitiveservices account delete --name \"{ai.Name}\" --resource-group \"{ai.ResourceGroup}\"",
            });
        }

        foreach (var workspace in r.LogAnalyticsInventory.Where(w => w.RiskLevel is "cost"))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = workspace.Name,
                ResourceGroup = workspace.ResourceGroup,
                Type = "Log Analytics",
                Source = "Log Analytics policy",
                Reason = workspace.Recommendation,
                Confidence = "medium",
                EstimatedMonthlyCost = "Variable ingestion",
                Command = null,
            });
        }

        // Catalog drift: an apps.json entry whose underlying resource has either gone
        // (no Microsoft.Web/sites matches the name) or no longer serves traffic. Surfaced
        // here so the existing dedup / priority-queue paths handle them, and the
        // human can copy the removal snippet into apps.json. We deliberately do not edit
        // the file — the catalog stays visible during outages, so the dashboard must
        // answer "remove it" with a finding the operator can act on, not a silent edit.
        foreach (var drift in r.CatalogDrift ?? new())
        {
            items.Add(new SafeToRemoveItem
            {
                Name = drift.Name,
                ResourceGroup = null,
                Type = "Catalogue",
                Source = "apps.json drift",
                Reason = drift.Reason,
                Confidence = drift.Confidence,
                EstimatedMonthlyCost = null,
                // No az command — the action lives on the filesystem, not on Azure.
                // The dashboard's "Copy" affordance reads the RemovalSnippet through
                // Command? when no shell command applies.
                Command = drift.RemovalSnippet,
            });
        }

        // Dedupe first, then order. A resource can be flagged by more than one scan loop —
        // a Microsoft.Web/sites with `broken + 0 requests` will appear as a "Connectivity +
        // Metrics" item (high confidence) AND as an "Orphaned resource scan" item (medium)
        // — and the queue must keep the highest-confidence verdict, not whichever scan ran
        // last. Doing the rank comparison inside each group makes that intent obvious; the
        // old code sorted the whole list by rank before grouping, which was correct only
        // because List<T>.Sort is stable AND GroupBy preserves encounter order, a pair of
        // guarantees nothing in the call site said it was relying on.
        //
        // The dedup key uses the resource type's last ARM segment ("sites" for
        // Microsoft.Web/sites) rather than the humanized display label. The connectivity
        // loop sets Type to `TypeLabel(...)` ("App Service") and the orphan loop uses
        // whatever label the upstream Cleanup scan produced ("App Service Plan" for a
        // serverFarm, but raw "Microsoft.Web/sites" if a future scan flags a Web App
        // directly) — those labels diverge, and keying on the display string meant the
        // dedup silently never fired across sources. The last segment is what identifies
        // the resource class in ARM regardless of which scanner wrote the entry.
        return items
            .GroupBy(i => DedupKey(i), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => SeverityLevel.Rank(i.Confidence)).First())
            .OrderBy(i => SeverityLevel.Rank(i.Confidence))
            .ToList();
    }

/// <summary>
    /// Canonical dedup key for a <see cref="SafeToRemoveItem"/>: the humanized type label
    /// (so "Microsoft.Web/sites" and a future "App Service" label collide on purpose), then
    /// the resource group, then the resource name. The connectivity loop runs every Type
    /// through <see cref="TypeLabel"/>; the orphan / plan / AI / workspace loops use the
    /// label the upstream scanner wrote. Routing both through TypeLabel here keeps the
    /// key stable regardless of which scanner wrote the entry.
    /// </summary>
    private static string DedupKey(SafeToRemoveItem i)
    {
        var normalized = TypeLabel(i.Type);
        return $"{normalized}|{i.ResourceGroup}|{i.Name}";
    }

    // ── Consolidated portfolio building ───────────────────────────────────────
    public static List<ConsolidatedService> BuildConsolidatedServices(AzureReport? r)
    {
        if (r is null)
            return new();
        var sourceServices = r.WebServices?.Services ?? new List<WebService>();
        if (sourceServices.Count == 0)
            return new();

        var grouped = sourceServices
            .GroupBy(s => CanonicalAppKey(s.FriendlyName, s.Name))
            .ToList();

        var raw = new List<ConsolidatedService>();
        foreach (var group in grouped)
        {
            var entries = group.ToList();
            var first = entries[0];
            var requests7d = entries.Sum(x => x.Metrics7Days?.Requests ?? 0);
            var http5xx7d = entries.Sum(x => x.Metrics7Days?.Http5xx ?? 0);
            var hasBroken = entries.Any(x =>
                x.HttpStatus is "broken" or "unreachable" ||
                x.Connectivity?.IsAzureErrorPage == true ||
                x.PlatformState == "Stopped");
            var rtCandidates = entries.Where(x => x.Connectivity?.Success == true)
                .Select(x => x.Connectivity!.ResponseTime).ToList();
            var responseMs = rtCandidates.Count > 0 ? (int?)Math.Round(rtCandidates.Average()) : null;
            var status = hasBroken ? "broken" : entries.Any(x => x.HttpStatus == "active") ? "active" : "other";

            var reliability = 100;
            reliability -= hasBroken ? 35 : 0;
            reliability -= Math.Min(30, http5xx7d * 3);
            reliability -= responseMs is > 3000 ? 20 : responseMs is > 1200 ? 10 : 0;
            reliability -= requests7d == 0 ? 10 : 0;
            reliability = Math.Clamp(reliability, 0, 100);

            var health = reliability;
            if (entries.Any(x => x.FreeTierCheck?.CanGoFree == true))
                health -= 5;
            health = Math.Clamp(health, 0, 100);

            raw.Add(new ConsolidatedService(
                Key: group.Key,
                DisplayName: string.IsNullOrWhiteSpace(first.FriendlyName) ? first.Name : first.FriendlyName,
                ResourceTypeSummary: string.Join(" + ", entries.Select(x => TypeLabel(x.ResourceType)).Distinct()),
                HttpStatus: status,
                Requests7d: requests7d,
                Http5xx7d: http5xx7d,
                ResponseTimeMs: responseMs,
                HealthScore: health,
                ReliabilityScore: reliability,
                Actionability: ToActionability(status, http5xx7d, requests7d),
                Owner: InferOwner(first.ResourceGroup, first.Name),
                Environment: InferEnvironment(first.ResourceGroup, first.Name),
                ResourceGroup: first.ResourceGroup,
                Command: first.ResourceType == "Microsoft.Web/sites"
                    ? $"az webapp show --name \"{first.Name}\" --resource-group \"{first.ResourceGroup}\""
                    : null
            ));
        }

        return raw
            .OrderBy(x => x.HealthScore)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    public static List<ResourceExplorerItem> BuildResourceExplorerItems(
        AzureReport? r,
        List<ConsolidatedService> consolidated,
        List<SafeToRemoveItem> safe)
    {
        if (r is null)
            return [];

        var servicesByName = (r.WebServices?.Services ?? [])
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var consolidatedByName = consolidated
            .GroupBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var cleanupNames = safe.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var securityNames = (r.StorageInventory ?? [])
            .Where(s => s.IssueCount > 0)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var driftNames = (r.ConfigDrift ?? [])
            .Where(d => d.IssueCount > 0)
            .SelectMany(d => new[] { d.Name, d.FriendlyName ?? "" })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resources = r.AllResourceSummary?.ResourcesByType.Values.SelectMany(items => items).ToList() ?? [];
        if (resources.Count == 0)
        {
            resources = (r.WebServices?.Services ?? []).Select(s => new ResourceDetail
            {
                Name = s.Name,
                ResourceGroup = s.ResourceGroup,
                Type = s.ResourceType,
                Sku = s.AppServicePlanSku,
            }).ToList();
        }

        var result = new List<ResourceExplorerItem>();
        foreach (var resource in resources
            .GroupBy(x => $"{x.Type}|{x.ResourceGroup}|{x.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()))
        {
            servicesByName.TryGetValue(resource.Name, out var service);
            var friendlyName = service?.FriendlyName;
            ConsolidatedService? portfolio = null;
            if (!string.IsNullOrWhiteSpace(friendlyName))
                consolidatedByName.TryGetValue(friendlyName, out portfolio);

            var risks = new List<string>();
            if (service?.HttpStatus is "broken" or "unreachable" || (service?.Metrics7Days?.Http5xx ?? 0) > 0)
                risks.Add("Unhealthy");
            if (cleanupNames.Contains(resource.Name) || (!string.IsNullOrWhiteSpace(friendlyName) && cleanupNames.Contains(friendlyName)))
                risks.Add("Waste");
            if (securityNames.Contains(resource.Name))
                risks.Add("Security");
            if (driftNames.Contains(resource.Name) || (!string.IsNullOrWhiteSpace(friendlyName) && driftNames.Contains(friendlyName)))
                risks.Add("Drift");

            result.Add(new ResourceExplorerItem(
                Name: string.IsNullOrWhiteSpace(friendlyName) ? resource.Name : friendlyName,
                Type: HumanizeResourceType(resource.Type?.Split('/').LastOrDefault() ?? "Resource"),
                ResourceGroup: resource.ResourceGroup ?? "—",
                Location: resource.Location ?? "—",
                Sku: resource.Sku ?? service?.AppServicePlanSku ?? "—",
                Status: service?.HttpStatus ?? (risks.Count == 0 ? "healthy" : "review"),
                Risk: risks.Count == 0 ? "None" : string.Join(", ", risks.Distinct()),
                Requests7d: portfolio?.Requests7d ?? service?.Metrics7Days?.Requests ?? 0,
                Errors7d: portfolio?.Http5xx7d ?? service?.Metrics7Days?.Http5xx ?? 0,
                ResponseTimeMs: portfolio?.ResponseTimeMs ?? service?.Connectivity?.ResponseTime
            ));
        }

        return result
            .OrderByDescending(i => i.Risk != "None")
            .ThenBy(i => i.Name)
            .ToList();
    }

    // ── Pure inference helpers ────────────────────────────────────────────────
    public static string CanonicalAppKey(string? friendly, string? name)
    {
        var source = string.IsNullOrWhiteSpace(friendly) ? (name ?? "unknown") : friendly;
        return source.Trim().ToLowerInvariant();
    }

    public static string InferEnvironment(string? resourceGroup, string? name)
    {
        var text = $"{resourceGroup} {name}".ToLowerInvariant();
        if (text.Contains("prod") || text.Contains("production"))
            return "prod";
        if (text.Contains("dev") || text.Contains("test") || text.Contains("staging"))
            return "dev";
        return "shared";
    }

    public static string InferOwner(string? resourceGroup, string? name)
    {
        var token = (resourceGroup ?? name ?? "")
            .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? "unassigned" : token.ToLowerInvariant();
    }

    /// <summary>
    /// Urgency order of the actionability tiers, most urgent first. The badge styles in
    /// <c>AzureDashboard.Presentation</c> encode the same ranking as colour; this is the
    /// sortable form of it. An unknown tier sorts last rather than first — a tier nobody
    /// named is not an emergency.
    /// </summary>
    public static int ActionabilityRank(string? tier) => tier switch
    {
        "Fix Now" => 0,
        "Fix Soon" => 1,
        "Remove Candidate" => 2,
        "Watch" => 3,
        _ => 4,
    };

    public static string ToActionability(string status, int http5xx7d, int req7d)
    {
        if (status == "broken" || http5xx7d > 10)
            return "Fix Now";
        if (http5xx7d > 0 || (status != "active" && req7d > 0))
            return "Fix Soon";
        if (status != "active" && req7d == 0)
            return "Remove Candidate";
        return "Watch";
    }

    // ── Uptime rows ────────────────────────────────────────────────────────────
    // Shared by AzureUptimeHeatmap (its screen-reader sentences) and AzureAppDetail (the
    // drawer's visible prose), which is why they left the heatmap's private @code block.

    /// <summary>
    /// Letters and digits only, lower-cased, so <c>?focus=pomemevideo</c>, an uptime row's
    /// <c>PoMemeVideo</c>, a sign-in roster's app name and an alert's link all match.
    /// </summary>
    public static string AppMatchKey(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    public static string DescribeUptimeState(string state) => state switch
    {
        UptimeState.Healthy => "healthy",
        UptimeState.Broken => "unavailable",
        UptimeState.Degraded => "degraded",
        _ => "no scan that day",
    };

    /// <summary>
    /// One uptime row as a sentence. Bad days are NAMED rather than counted, capped at four
    /// with an "and N more" tail: which days an app was down is the reason to look, and a
    /// 30-item date list read aloud is no more usable than the 30 cells it replaces. Days
    /// with no scan are never folded into the good or the bad — the percentage's own rule.
    /// </summary>
    public static string DescribeUptimeRow(UptimeRow row, IReadOnlyList<DateTime> days)
    {
        if (row.ScansObserved == 0)
            return $"{row.Name}: never observed in this window.";

        var bad = new List<string>();
        for (var i = 0; i < row.Cells.Count && i < days.Count; i++)
        {
            if (row.Cells[i] is UptimeState.Broken or UptimeState.Degraded)
                bad.Add($"{days[i]:MMM d} {DescribeUptimeState(row.Cells[i])}");
        }

        var text = $"{row.Name}: healthy on {row.UptimePercent} percent of {Plural(row.ScansObserved, "check")} "
            + $"across {Plural(row.DaysWithData, "observed day")} of {days.Count}.";

        if (row.Errors24h is > 0)
            text += $" {Plural(row.Errors24h.Value, "exception")} in the last 24 hours{(row.TopError is { } t ? $", mostly {t}" : "")}.";
        if (row.SecurityAlerts is > 0)
            text += $" {Plural(row.SecurityAlerts.Value, "open Dependabot alert")}.";
        if (row.Packages is { Vulnerable.Count: > 0 } vulnerable)
            text += $" {Plural(vulnerable.Vulnerable.Count, "package")} with a known vulnerability.";
        if (row.LastRun?.Conclusion == "failure")
            text += $" Latest build failed on {row.LastRun.CompletedAt:MMM d}.";
        if (row.ContentCheck is { Found: false, Error: null } check)
            text += $" The page answers but no longer contains \"{check.Expect}\".";

        if (row.DeployDays.Count > 0)
            text += $" Deployed {string.Join(", ", row.DeployDays.Where(i => i < days.Count).Select(i => $"{days[i]:MMM d}"))}.";

        if (bad.Count == 0)
            return text + " No bad days.";

        var named = string.Join(", ", bad.Take(4));
        return bad.Count <= 4
            ? $"{text} Bad days: {named}."
            : $"{text} Bad days: {named}, and {bad.Count - 4} more.";
    }

    /// <summary>
    /// The shield chip's number: open Dependabot alerts when the repo has Dependabot on, else
    /// the OSV.dev count from the package scan. Null — no chip — when neither is known.
    /// </summary>
    public static int? VulnerabilityCount(UptimeRow row) => row.SecurityAlerts ?? row.Packages?.Vulnerable.Count;

    /// <summary>"1 check", "3 checks" — instead of the "(s)" the UI used to print.</summary>
    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
}
