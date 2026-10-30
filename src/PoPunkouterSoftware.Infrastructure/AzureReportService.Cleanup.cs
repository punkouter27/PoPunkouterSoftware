using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Steps 10-12: the cleanup candidates - zombie apps, apps.json drift, and orphaned resources.
/// </summary>
public partial class AzureReportService
{
    private static List<ZombieApp> DetectZombies(List<RawService> services, Dictionary<string, MetricsInfo> metricsMap)
        => services
            .Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null)
            // Exclude WebSocket-only, SignalR, and background worker services:
            // - SignalR hubs (kind: "signalr") don't serve HTTP pages
            // - WebJob/background services are not front-end web apps
            // - Kind containing "functionapp" are Azure Functions, not web apps
            .Where(s => !string.IsNullOrEmpty(s.Kind) &&
                        !s.Kind.Contains("signalr", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("functionapp", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("workflowapp", StringComparison.OrdinalIgnoreCase) &&
                        s.PlatformState != "Stopped")
            .Where(s => metricsMap.TryGetValue(s.ResourceId!, out var m) && m.Requests == 0)
            .Select(s => new ZombieApp
            {
                Name = s.Name,
                ResourceGroup = s.ResourceGroup,
                HttpStatus = s.HttpStatus,
                PlatformState = s.PlatformState,
                Recommendation = $"az webapp stop --name \"{s.Name}\" --resource-group \"{s.ResourceGroup}\"",
            })
            .ToList();

    private async Task<List<OrphanedResource>> GetOrphanedResourcesAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return [];
        var client = _httpClientFactory.CreateClient("azure-arm");

        // Fetches the ARM detail for one resource and hands the parsed JSON to `evaluate`,
        // which returns the orphan finding (or null when the resource is not orphaned).
        // Shared by all three categories below — they previously hand-rolled the same
        // request/parse/try-catch around three different type filters and evaluations.
        // Concurrency is gated by the shared BoundedParallelAsync call below, not here.
        async Task<OrphanedResource?> CheckAsync(
            GenericResourceData resource, string urlSuffix, string category,
            Func<JsonElement, GenericResourceData, OrphanedResource?> evaluate)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{resource.Id}{urlSuffix}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return evaluate(doc.RootElement, resource);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Category} orphan check failed for {Name}", category, resource.Name);
                return null;
            }
        }

        var diskItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase))
            .Select(disk => (Resource: disk, UrlSuffix: "?api-version=2023-10-02", Category: "Disk", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                if (!root.TryGetProperty("properties", out var props))
                    return null;
                if (!props.TryGetProperty("diskState", out var state) || state.GetString() != "Unattached")
                    return null;

                var sizeGb = props.TryGetProperty("diskSizeGB", out var sz) ? sz.GetInt32() : 0;
                var sku = root.TryGetProperty("sku", out var skuEl) &&
                             skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "Managed Disk",
                    Reason = $"Unattached ({sizeGb} GB, {sku ?? "unknown SKU"})",
                    EstimatedMonthlyCost = sizeGb > 0 ? $"~${sizeGb * 0.04:F2}/mo" : null,
                    Command = $"az disk delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\" --yes",
                };
            })));

        var ipItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Network/publicIPAddresses", StringComparison.OrdinalIgnoreCase))
            .Select(ip => (Resource: ip, UrlSuffix: "?api-version=2023-11-01", Category: "Public IP", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                if (!root.TryGetProperty("properties", out var props))
                    return null;

                var hasIpConfig = props.TryGetProperty("ipConfiguration", out _);
                var hasNatGateway = props.TryGetProperty("natGateway", out _);
                if (hasIpConfig || hasNatGateway)
                    return null;

                var sku = root.TryGetProperty("sku", out var skuEl) &&
                          skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "Public IP",
                    Reason = $"Not associated with any NIC or NAT gateway (SKU: {sku ?? "—"})",
                    EstimatedMonthlyCost = sku == "Standard" ? "~$3.65/mo" : null,
                    Command = $"az network public-ip delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\"",
                };
            })));

        var farmItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Web/serverFarms", StringComparison.OrdinalIgnoreCase))
            .Select(farm => (Resource: farm, UrlSuffix: "/sites?api-version=2023-12-01", Category: "App Service Plan", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                var siteCount = root.TryGetProperty("value", out var v) ? v.GetArrayLength() : 1;
                if (siteCount > 0)
                    return null;

                var sku = r.Sku?.Name?.ToString() ?? "unknown";
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "App Service Plan",
                    Reason = $"No apps deployed (SKU: {sku})",
                    EstimatedMonthlyCost = sku is "F1" or "FREE" ? "$0/mo (Free)" : "Paid tier — check portal",
                    Command = $"az appservice plan delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\" --yes",
                };
            })));

        // BoundedParallelAsync preserves input order (disks, then IPs, then plans) regardless
        // of completion order, so the result ordering matches the previous sequential passes.
        var results = await BoundedParallelAsync(
            diskItems.Concat(ipItems).Concat(farmItems), maxConcurrency: 6,
            item => CheckAsync(item.Resource, item.UrlSuffix, item.Category, item.Evaluate), ct);
        return results.OfType<OrphanedResource>().ToList();
    }

    // ── Step 12: apps.json drift ──────────────────────────────────────────────
    //
    // The catalog at wwwroot/data/apps.json is the home-page source of truth. The scan
    // only decorates it — a probe failure must not blank a card. But "doesn't exist
    // any more" is the user's explicit case: the Azure resource is gone, or the URL no
    // longer loads, and the card is a dead link. This step emits one finding per dead
    // catalog entry with the exact JSON to delete; the catalog itself stays unchanged.
    //
    // Two reasons this lives in the scan and not in /api/portfolio:
    //   1. The probe needs an HTTP client (which the scan already has), not the
    //      request-scoped handler chain that the request path uses.
    //   2. Findings live in the report, persist with it through Table Storage, and
    //      round-trip to /api/diag/summary's attention list alongside zombies and
    //      orphans — so a human sees the cleanup queue without a separate endpoint.
    private async Task<List<CatalogDriftItem>> DetectCatalogDriftAsync(
        List<RawService> services, CancellationToken ct)
    {
        var catalogPath = Path.Combine(ReportFileCache.GetCatalogDir(_env), "apps.json");
        if (!File.Exists(catalogPath))
            return [];

        string rawJson;
        try
        {
            rawJson = await File.ReadAllTextAsync(catalogPath, ct);
        }
        catch (IOException ex)
        {
            // Same degradation posture as the file-cache loader: a transient read error
            // does not abort the scan. Drift is informational; better to miss one cycle
            // than to fail the whole refresh.
            _logger.LogWarning(ex, "apps.json could not be read for drift detection — skipping this step");
            return [];
        }

        AppsFile? wrapper;
        try
        {
            wrapper = System.Text.Json.JsonSerializer.Deserialize<AppsFile>(rawJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "apps.json is malformed — skipping drift detection");
            return [];
        }

        var activeEntries = wrapper?.Apps?
            .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                        && string.Equals(a.Status, "active", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<AppMeta>();
        if (activeEntries.Count == 0)
            return [];

        // The matching rule mirrors /api/portfolio: the catalog Name collapses onto the
        // scan's ServiceIdentity (friendly name when present, resource name otherwise).
        // A live resource matching any catalog entry by that key means the entry is NOT
        // drifted, regardless of which side has the richer data. We use the same letters-
        // and-digits-only normalize that PortfolioIdentity defines on the API side —
        // duplicating it here is cheaper than coupling the Infrastructure slice to the
        // API slice for a one-line pure helper, and lets the detector be Unit-tested
        // without standing up the API host.
        var liveIdentities = new HashSet<string>(
            services.Select(s => NormalizeKey(s.FriendlyName, s.Name)),
            StringComparer.OrdinalIgnoreCase);

        var drifted = new List<CatalogDriftItem>();
        // Probe URLs in parallel; the catalog is small (~12 entries) and each call has a
        // bounded per-request timeout, so unbounded parallelism is fine.
        var probeTasks = new List<Task<(AppMeta Meta, bool Loaded, int? StatusCode, string? Reason)>>();
        foreach (var meta in activeEntries)
        {
            var identity = NormalizeKey(meta.Name);
            if (liveIdentities.Contains(identity))
                continue;

            probeTasks.Add(ProbeCatalogUrlAsync(meta, ct));
        }

        if (probeTasks.Count == 0)
            return drifted;

        var probed = await Task.WhenAll(probeTasks);

        foreach (var (meta, loaded, statusCode, reason) in probed)
        {
            CatalogDriftItem? item = null;
            if (!loaded)
            {
                // URL probe failed. The Azure resource is also absent (we already filtered
                // out entries that match a live service). Surface as a single "doesn't load"
                // finding — the URL is the actionable detail, and the snippet removes the
                // entry regardless of which reason triggered it.
                item = new CatalogDriftItem
                {
                    Name = meta.Name ?? "",
                    Url = meta.Url,
                    Kind = "url-not-loadable",
                    Reason = reason ?? $"Probe to {meta.Url} failed (status {(statusCode.HasValue ? statusCode.Value.ToString() : "—")}).",
                    Confidence = "medium",
                    RemovalSnippet = BuildRemovalSnippet(meta, rawJson),
                };
            }
            else
            {
                // URL still answers, but no Azure resource exists with this name. The app
                // is being served from outside the scanned subscription (a static URL on
                // a different host) OR the resource was renamed. Either way the home page
                // will keep rendering this card forever until the entry is removed or the
                // name is repaired.
                item = new CatalogDriftItem
                {
                    Name = meta.Name ?? "",
                    Url = meta.Url,
                    Kind = "missing-in-azure",
                    Reason = $"No Microsoft.Web/sites found in this subscription matching '{meta.Name}'. The URL still answers, so the app may live elsewhere — rename the catalog entry or remove it.",
                    Confidence = "high",
                    RemovalSnippet = BuildRemovalSnippet(meta, rawJson),
                };
            }
            drifted.Add(item);
        }
        return drifted;
    }

    private async Task<(AppMeta Meta, bool Loaded, int? StatusCode, string? Reason)> ProbeCatalogUrlAsync(
        AppMeta meta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(meta.Url))
            return (meta, false, null, "Catalog entry has no URL to probe.");

        var client = _httpClientFactory.CreateClient("azure-probe");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, meta.Url);
            using var resp = await client.SendAsync(req, ct);
            // A 405 from a server that doesn't allow HEAD is still a live host: it
            // answered the request, not an error. Same for 3xx — we follow redirects
            // (the typed client is configured that way), so a 3xx here means the chain
            // eventually failed to resolve a final 2xx/3xx.
            var status = (int)resp.StatusCode;
            if (status is >= 200 and < 400)
                return (meta, true, status, null);
            if (status is 405 or 403)
                return (meta, true, status, $"Head request refused ({status}); the host answered.");
            return (meta, false, status, $"Probe to {meta.Url} returned HTTP {status}.");
        }
        catch (TaskCanceledException)
        {
            return (meta, false, null, $"Probe to {meta.Url} timed out.");
        }
        catch (HttpRequestException ex)
        {
            return (meta, false, null, $"Probe to {meta.Url} failed: {ex.Message}");
        }
    }

    // Slices the raw apps.json text into the exact lines of the catalog entry's object
    // literal so the dashboard's "Copy removal snippet" button hands the operator a
    // patch they can paste straight into the file. Indentation is preserved by tracking
    // the leading whitespace of the opening brace.
    private static string? BuildRemovalSnippet(AppMeta meta, string rawJson)
    {
        if (string.IsNullOrEmpty(meta.Id) && string.IsNullOrEmpty(meta.Name))
            return null;

        var lines = rawJson.Split('\n');
        int startIdx = -1, endIdx = -1;
        string lead = "    ";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (startIdx < 0)
            {
                // Anchor on the id field — id is the only key apps.json requires to be
                // unique, so it is the safest match. We accept a match where the line
                // contains `"id": "<meta.Id>"` (catalog id, lowercase / kebab) OR the
                // literal name on its own line; id is preferred because the file uses
                // it as the unique key.
                var idMatch = !string.IsNullOrEmpty(meta.Id)
                    && line.Contains($"\"id\": \"{meta.Id}\"", StringComparison.Ordinal);
                var nameMatch = !string.IsNullOrEmpty(meta.Name)
                    && line.Contains($"\"name\": \"{meta.Name}\"", StringComparison.Ordinal);
                if (!idMatch && !nameMatch)
                    continue;

                // Walk backwards to the opening brace of this object.
                for (var j = i; j >= 0; j--)
                {
                    if (lines[j].Contains('{'))
                    {
                        startIdx = j;
                        lead = new string(' ', lines[j].TakeWhile(char.IsWhiteSpace).Count());
                        break;
                    }
                }
                if (startIdx < 0) return null;
            }

            if (startIdx >= 0 && i > startIdx && lines[i].TrimStart() == "}" + (lines[i].Contains('}') ? "" : ""))
            {
                // Closing brace at the same or greater indent than the opening.
                if (lines[i].IndexOf('}') >= 0)
                {
                    endIdx = i;
                    break;
                }
            }
        }

        if (startIdx < 0 || endIdx < 0)
            return null;

        // Include the comma on the previous line if present (between entries), or the
        // comma at the end of our closing line if present.
        var snippet = string.Join("\n", lines[startIdx..(endIdx + 1)]);
        return snippet.TrimEnd();
    }

    // Mirrors the file shape PortfolioEndpoints uses, scoped to this slice so the
    // infrastructure assembly can read apps.json without depending on the API slice.
    private sealed record AppsFile(List<AppMeta>? Apps);
    private sealed record AppMeta(
        string? Id,
        string? Name,
        string? Description,
        string? Category,
        string? Status,
        string? Url,
        System.Collections.Generic.List<string>? Technologies,
        string? GithubRepo,
        string? Expect = null);

    private static string NormalizeKey(string? friendlyName, string? resourceName) =>
        string.IsNullOrWhiteSpace(friendlyName)
            ? (resourceName ?? "")
            : friendlyName;

    private static string NormalizeKey(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
