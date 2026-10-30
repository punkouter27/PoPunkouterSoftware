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
/// Steps 5 and 9: 30-day spend from Cost Management and monthly burn rate. Also hosts the
/// per-service free-tier lookup (<see cref="CheckFreeTierForService"/>) shared with the
/// orchestrator when it decorates each discovered service.
/// </summary>
public partial class AzureReportService
{
    // ── Step 5: 30-day cost via Cost Management REST ───────────────────────────

/// <summary>Fetches 30-day usage and cost from the Cost Management API; calculates monthly burn rate.</summary>

    private async Task<CostInfo> GetCostAsync(string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return new CostInfo { Note = "Cost data unavailable (no ARM token)" };
        try
        {
            var today = DateTime.UtcNow.Date;
            var start = today.AddDays(-30);
            var body = JsonSerializer.Serialize(new
            {
                type = "Usage",
                timeframe = "Custom",
                timePeriod = new { from = start.ToString("yyyy-MM-dd"), to = today.ToString("yyyy-MM-dd") },
                dataset = new
                {
                    granularity = "None",
                    aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
                    grouping = new[]
                    {
                        new { type = "Dimension", name = "ServiceName" },
                        new { type = "Dimension", name = "ResourceGroupName" },
                    },
                },
            });

            var result = await QueryCostManagementAsync(subscriptionId, body, armToken, (rows, cols) =>
            {
                int costIdx = cols.FindIndex(c => c.Contains("pretax") || c.Contains("cost"));
                int svcIdx = cols.FindIndex(c => c.Contains("service"));
                int rgIdx = cols.FindIndex(c => c.Contains("resourcegroup"));

                double totalCost = 0;
                var byKey = new Dictionary<string, double>();
                foreach (var row in rows)
                {
                    var arr = row.EnumerateArray().ToArray();
                    var cost = costIdx >= 0 ? arr[costIdx].GetDouble() : 0;
                    var svc = svcIdx >= 0 ? arr[svcIdx].GetString() ?? "Unknown" : "Unknown";
                    var rg = rgIdx >= 0 ? arr[rgIdx].GetString() ?? "" : "";
                    var key = string.IsNullOrEmpty(rg) ? svc : $"{svc} ({rg})";
                    byKey[key] = byKey.GetValueOrDefault(key) + cost;
                    totalCost += cost;
                }

                var drivers = byKey
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Take(20)
                    .Select(kv => new CostDriver { Name = kv.Key, Cost = Math.Round(kv.Value, 4) })
                    .ToList();

                return new CostInfo
                {
                    TotalCost30Days = Math.Round(totalCost, 4),
                    TotalFormatted = $"${totalCost:F2}",
                    TopCostDrivers = drivers,
                    Note = totalCost == 0 ? "All costs $0.00 — subscription may be covered by credits." : null,
                };
            }, ct);

            return result ?? new CostInfo { Note = "Cost data unavailable (rate-limited or request failed)" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cost analysis failed");
            return new CostInfo { Note = $"Cost data unavailable: {ex.Message}" };
        }
    }

    /// <summary>
    /// 30 days of spend per resource group per day — the series the spike detector and the
    /// /users cost-per-person join read. A separate query from <see cref="GetCostAsync"/>
    /// because adding a daily dimension to that one would multiply its rows past Cost
    /// Management's 1,000-row page, which is not followed here.
    /// </summary>
    // ponytail: no nextLink paging; 30 days x ~30 groups stays under the 1,000-row page. Follow
    // properties.nextLink if the subscription grows past ~33 billed resource groups.
    private async Task<List<ResourceGroupCost>> GetResourceGroupCostsAsync(
        string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return [];
        try
        {
            var today = DateTime.UtcNow.Date;
            var body = JsonSerializer.Serialize(new
            {
                type = "Usage",
                timeframe = "Custom",
                timePeriod = new { from = today.AddDays(-30).ToString("yyyy-MM-dd"), to = today.ToString("yyyy-MM-dd") },
                dataset = new
                {
                    granularity = "Daily",
                    aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
                    grouping = new[] { new { type = "Dimension", name = "ResourceGroupName" } },
                },
            });

            var result = await QueryCostManagementAsync(subscriptionId, body, armToken, (rows, cols) =>
            {
                int costIdx = cols.FindIndex(c => c.Contains("pretax") || c.Contains("cost"));
                int dateIdx = cols.FindIndex(c => c.Contains("date") || c.Contains("usage"));
                int rgIdx = cols.FindIndex(c => c.Contains("resourcegroup"));
                if (costIdx < 0 || dateIdx < 0 || rgIdx < 0)
                    return new List<ResourceGroupCost>();

                return rows
                    .Select(row => row.EnumerateArray().ToArray())
                    .Select(arr => (
                        Rg: arr[rgIdx].GetString() ?? "",
                        Date: CostDate(arr[dateIdx]),
                        Cost: arr[costIdx].GetDouble()))
                    .Where(r => r.Rg.Length > 0 && r.Cost > 0)
                    // Cost Management lower-cases some group names and not others; one group per name.
                    .GroupBy(r => r.Rg, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ResourceGroupCost
                    {
                        Name = g.Key,
                        Total = Math.Round(g.Sum(r => r.Cost), 4),
                        Daily = g.GroupBy(r => r.Date)
                            .Select(d => new DailyCostEntry { Date = d.Key, Cost = Math.Round(d.Sum(r => r.Cost), 4) })
                            .OrderBy(d => d.Date, StringComparer.Ordinal)
                            .ToList(),
                    })
                    .OrderByDescending(g => g.Total)
                    .ToList();
            }, ct);

            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Per-resource-group daily cost query failed");
            return [];
        }
    }

    /// <summary>Cost Management returns a day as the number 20260926 or as a string; always yyyy-MM-dd out.</summary>
    private static string CostDate(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString() : value.GetString() ?? "";
        return raw.Length == 8 && raw.All(char.IsDigit) ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}"
            : raw.Length >= 10 ? raw[..10]
            : raw;
    }

    private async Task<BurnRateInfo?> GetBurnRateAsync(
        string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return null;
        try
        {
            var today = DateTime.UtcNow.Date;
            var startOfMonth = new DateTime(today.Year, today.Month, 1);
            if (startOfMonth == today)
                startOfMonth = today.AddDays(-1);

            var body = JsonSerializer.Serialize(new
            {
                type = "Usage",
                timeframe = "Custom",
                timePeriod = new { from = startOfMonth.ToString("yyyy-MM-dd"), to = today.ToString("yyyy-MM-dd") },
                dataset = new
                {
                    granularity = "Daily",
                    aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
                },
            });

            var result = await QueryCostManagementAsync(subscriptionId, body, armToken, (rows, cols) =>
            {
                int costIdx = cols.FindIndex(c => c.Contains("pretax") || c.Contains("cost"));
                int dateIdx = cols.FindIndex(c => c.Contains("date") || c.Contains("usage"));

                var entries = new List<DailyCostEntry>();
                foreach (var row in rows)
                {
                    var arr = row.EnumerateArray().ToArray();
                    var cost = costIdx >= 0 ? arr[costIdx].GetDouble() : 0;
                    var raw = dateIdx >= 0
                        ? arr[dateIdx].ValueKind == JsonValueKind.Number
                            ? arr[dateIdx].GetInt32().ToString()
                            : arr[dateIdx].GetString() ?? ""
                        : "";
                    var dateStr = raw.Length == 8 && raw.All(char.IsDigit)
                        ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}"
                        : raw;
                    entries.Add(new DailyCostEntry { Date = dateStr, Cost = Math.Round(cost, 4) });
                }
                return entries;
            }, ct);

            if (result is null)
                return null;

            var daily = result.OrderBy(d => d.Date).ToList();
            var totalSoFar = daily.Sum(d => d.Cost);
            var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
            var daysElapsed = Math.Max(1, (today - startOfMonth).Days + 1);
            var projected = Math.Round(totalSoFar / daysElapsed * daysInMonth, 2);

            return new BurnRateInfo
            {
                DailyCosts = daily,
                ProjectedMonthTotal = projected,
                ProjectedFormatted = $"${projected:F2}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Burn rate query failed");
            return null;
        }
    }

    /// <summary>
    /// POSTs a Cost Management query and hands the parsed rows/column names to
    /// <paramref name="parseRows"/> while the underlying <see cref="JsonDocument"/> is still
    /// alive — JsonElements do not survive their document being disposed. Shared by
    /// <see cref="GetCostAsync"/> and <see cref="GetBurnRateAsync"/>, which previously
    /// duplicated this POST-then-parse-the-envelope boilerplate; only the row processing
    /// (grouped service+RG totals vs. a daily series) actually differs between them.
    /// </summary>
    private async Task<TResult?> QueryCostManagementAsync<TResult>(
        string subscriptionId, string requestBody, string? armToken,
        Func<List<JsonElement>, List<string>, TResult> parseRows, CancellationToken ct)
        where TResult : class
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
        var json = await PostCostManagementWithRetryAsync(url, requestBody, armToken, ct);
        if (json is null)
            return null;

        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");
        var rows = props.GetProperty("rows").EnumerateArray().ToList();
        var cols = props.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!.ToLowerInvariant()).ToList();

        return parseRows(rows, cols);
    }

    /// <summary>
    /// POSTs to the Cost Management API with a bespoke retry loop for 429 responses.
    /// </summary>
    private async Task<string?> PostCostManagementWithRetryAsync(
        string url, string body, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return null;
        // Deliberately the unnamed client: this method owns its own retry loop (429 with
        // Retry-After can require waits far beyond a generic pipeline's budget).
        var client = _httpClientFactory.CreateClient();
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                // Transient network fault (socket reset, DNS blip) — not just 429s are transient.
                _logger.LogWarning(ex, "Cost Management request failed; retrying (attempt {Attempt}/{Max})",
                    attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct);
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxRetries - 1)
                {
                    int delaySec = 30;
                    if (resp.Headers.RetryAfter?.Delta.HasValue == true)
                        delaySec = (int)resp.Headers.RetryAfter.Delta!.Value.TotalSeconds;
                    else if (resp.Headers.RetryAfter?.Date.HasValue == true)
                        delaySec = (int)(resp.Headers.RetryAfter.Date!.Value - DateTimeOffset.UtcNow).TotalSeconds;

                    delaySec = Math.Clamp(delaySec, 1, 65);
                    _logger.LogWarning("Cost Management returned TooManyRequests; retrying in {Delay}s (attempt {Attempt}/{Max})",
                        delaySec, attempt + 1, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                    continue;
                }

                if ((int)resp.StatusCode >= 500 && attempt < maxRetries - 1)
                {
                    _logger.LogWarning("Cost Management returned {Status}; retrying (attempt {Attempt}/{Max})",
                        resp.StatusCode, attempt + 1, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct);
                    continue;
                }

                _logger.LogWarning("Cost Management returned {Status}", resp.StatusCode);
                return null;
            }
        }
        return null;
    }

    private static FreeTierCheckInfo? CheckFreeTierForService(string typeKey, string? sku)
    {
        if (!FreeTierMap.TryGetValue(typeKey, out var info))
            return null;
        var isOnFree = info.FreeSku is not null &&
                       string.Equals(sku, info.FreeSku, StringComparison.OrdinalIgnoreCase);
        return new FreeTierCheckInfo
        {
            IsOnFreeTier = isOnFree,
            IsOnPaidTier = !isOnFree && info.PaidSkus.Any(p => string.Equals(sku, p, StringComparison.OrdinalIgnoreCase)),
            CanGoFree = info.FreeSku is not null && !isOnFree,
        };
    }

    private record FreeTierEntry(string Label, string? FreeSku, string FreeSkuLabel, string[] PaidSkus, string Note);

    private static readonly Dictionary<string, FreeTierEntry> FreeTierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Web/sites"] = new("App Service", "F1", "Free (F1)", ["B1", "B2", "B3", "S1", "S2", "S3", "P1V2", "P2V2", "P3V2"], "F1 provides 60 CPU-min/day."),
        ["Microsoft.Web/serverFarms"] = new("App Service Plan", "F1", "Free (F1)", ["B1", "B2", "B3", "S1", "S2", "S3"], "Downgrade to F1 if traffic is low."),
        ["Microsoft.Web/staticSites"] = new("Static Web App", "Free", "Free", ["Standard"], "Free tier: 100 GB bandwidth/month."),
        ["Microsoft.App/containerApps"] = new("Container App", null, "180k vCPU-s free/month", ["Consumption"], "Set min-replicas=0 to stay in free quota."),
        ["Microsoft.ContainerRegistry/registries"] = new("Container Registry", null, "No free tier", ["Basic", "Standard", "Premium"], "Basic ~$5/mo. Consider ghcr.io for free private images."),
        ["Microsoft.DocumentDB/databaseAccounts"] = new("Cosmos DB", "Free", "Free tier (1000 RU/s + 25 GB)", ["Standard"], "One free Cosmos DB per subscription."),
        ["Microsoft.Sql/servers/databases"] = new("Azure SQL", "Free", "Free offer (32 GB serverless)", ["Basic", "Standard", "Premium"], "One free Azure SQL per subscription."),
        ["Microsoft.Storage/storageAccounts"] = new("Storage Account", null, "5 GB Blob free/month (12 mo)", ["Standard_LRS", "Standard_GRS"], "Use LRS for lowest cost."),
        ["Microsoft.CognitiveServices/accounts"] = new("Azure AI / Cognitive", "F0", "Free (F0)", ["S0", "S1"], "F0 sufficient for dev/hobby use."),
        ["Microsoft.Search/searchServices"] = new("Azure AI Search", "free", "Free (1 svc, 3 indexes, 50 MB)", ["basic", "standard"], "One free search service per subscription."),
        ["microsoft.insights/components"] = new("Application Insights", null, "5 GB/month free ingestion", ["pergb2018"], "Enable adaptive sampling to stay under 5 GB/month."),
        ["Microsoft.OperationalInsights/workspaces"] = new("Log Analytics", "Free", "Free (500 MB/day)", ["PerGB2018", "Standard"], "Set a data cap on paid SKUs."),
        ["Microsoft.KeyVault/vaults"] = new("Key Vault", null, "~$0.03 per 10k ops", ["standard", "premium"], "Consolidate vaults when possible."),
        ["Microsoft.Network/publicIPAddresses"] = new("Public IP", null, "First 5 Basic static IPs free", ["Standard"], "Delete IPs not attached to any resource."),
        ["Microsoft.ServiceBus/namespaces"] = new("Service Bus", null, "No free tier — Basic ~$0.05/M ops", ["Basic", "Standard", "Premium"], "Use Basic if only simple queues needed."),
        ["Microsoft.SignalRService/SignalR"] = new("SignalR", "Free", "Free (20 connections)", ["Standard"], "Free tier: 20 concurrent connections."),
    };
}
