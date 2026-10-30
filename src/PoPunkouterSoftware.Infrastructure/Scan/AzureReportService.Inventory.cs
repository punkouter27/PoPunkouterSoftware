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
/// Steps 8b-8c: AI services and Log Analytics workspace inventory.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<AiServiceInventoryItem>> GetAiServicesInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var accounts = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.CognitiveServices/accounts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (accounts.Count == 0)
            return [];

        var client = _httpClientFactory.CreateClient("azure-arm");

        async Task<AiServiceInventoryItem> InspectAsync(GenericResourceData account)
        {
            string? endpoint = null;
            var deployments = new List<string>();

            if (armToken is not null)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{account.Id}?api-version=2023-05-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props)
                            && props.TryGetProperty("endpoint", out var ep))
                            endpoint = ep.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AI service detail fetch failed for {Name}", account.Name);
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{account.Id}/deployments?api-version=2023-05-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var value)
                            && value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var deployment in value.EnumerateArray())
                            {
                                var name = deployment.TryGetProperty("name", out var nameEl)
                                    ? nameEl.GetString()
                                    : null;
                                var model = deployment.TryGetProperty("properties", out var props)
                                    && props.TryGetProperty("model", out var modelEl)
                                    && modelEl.TryGetProperty("name", out var modelName)
                                        ? modelName.GetString()
                                        : null;
                                deployments.Add(string.IsNullOrWhiteSpace(model) ? name ?? "deployment" : $"{name} ({model})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AI deployment fetch failed for {Name}", account.Name);
                }
            }

            var sku = account.Sku?.Name?.ToString();
            var kind = account.Kind;
            var risk = ResourceRiskLevel.Watch;
            var recommendation = "Track usage and keep budget alerts enabled.";

            if (string.Equals(sku, "S0", StringComparison.OrdinalIgnoreCase) && deployments.Count == 0)
            {
                risk = ResourceRiskLevel.Cleanup;
                recommendation = "S0 account has no deployments. Confirm it is unused, then delete or downgrade.";
            }
            else if (string.Equals(sku, "S0", StringComparison.OrdinalIgnoreCase))
            {
                risk = ResourceRiskLevel.Cost;
                recommendation = "Paid AI account. Review token/call volume and cap spend with Azure budgets.";
            }
            else if (string.Equals(sku, "F0", StringComparison.OrdinalIgnoreCase))
            {
                risk = ResourceRiskLevel.Ok;
                recommendation = "Free-tier account. Keep unless it duplicates another AI endpoint.";
            }

            return new AiServiceInventoryItem
            {
                Name = account.Name,
                ResourceGroup = account.Id?.ResourceGroupName,
                Location = account.Location.Name,
                Kind = kind,
                Sku = sku,
                Endpoint = endpoint,
                DeploymentCount = deployments.Count,
                Deployments = deployments.OrderBy(x => x).ToList(),
                Recommendation = recommendation,
                RiskLevel = risk,
            };
        }

        var results = await Task.WhenAll(accounts.Select(InspectAsync));
        // Rank-based ordering: sorting the raw strings alphabetically happened to yield
        // cleanup < cost < ok < watch — accidental semantics that broke the moment a new
        // level was added. Rank() encodes "most actionable first" explicitly.
        return results.OrderBy(x => ResourceRiskLevel.Rank(x.RiskLevel)).ThenBy(x => x.Name).ToList();
    }

    private async Task<List<LogAnalyticsWorkspaceItem>> GetLogAnalyticsInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var workspaces = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.OperationalInsights/workspaces", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (workspaces.Count == 0)
            return [];

        var client = _httpClientFactory.CreateClient("azure-arm");

        async Task<LogAnalyticsWorkspaceItem> InspectAsync(GenericResourceData workspace)
        {
            string? sku = workspace.Sku?.Name?.ToString();
            int? retention = null;
            double? dailyQuota = null;

            if (armToken is not null)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{workspace.Id}?api-version=2022-10-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props))
                        {
                            if (props.TryGetProperty("retentionInDays", out var ret) && ret.ValueKind == JsonValueKind.Number)
                                retention = ret.GetInt32();
                            if (props.TryGetProperty("workspaceCapping", out var cap)
                                && cap.TryGetProperty("dailyQuotaGb", out var quota)
                                && quota.ValueKind == JsonValueKind.Number)
                                dailyQuota = quota.GetDouble();
                        }
                        if (doc.RootElement.TryGetProperty("sku", out var skuEl)
                            && skuEl.TryGetProperty("name", out var skuName))
                            sku = skuName.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Log Analytics detail fetch failed for {Name}", workspace.Name);
                }
            }

            var risk = ResourceRiskLevel.Ok;
            var recommendation = "Retention and cap look reasonable.";
            if (dailyQuota is null or <= 0)
            {
                risk = ResourceRiskLevel.Cost;
                recommendation = "No daily ingestion cap is visible. Set a cap such as 0.5-1 GB/day for hobby apps.";
            }
            else if (retention is > 30)
            {
                risk = ResourceRiskLevel.Cost;
                recommendation = "Retention is above 30 days. Reduce to 7-14 days if long history is not needed.";
            }
            else if (retention is null)
            {
                risk = ResourceRiskLevel.Watch;
                recommendation = "Retention was not reported. Verify retention and daily cap in Azure.";
            }

            return new LogAnalyticsWorkspaceItem
            {
                Name = workspace.Name,
                ResourceGroup = workspace.Id?.ResourceGroupName,
                Location = workspace.Location.Name,
                Sku = sku,
                RetentionInDays = retention,
                DailyQuotaGb = dailyQuota,
                Recommendation = recommendation,
                RiskLevel = risk,
            };
        }

        var results = await Task.WhenAll(workspaces.Select(InspectAsync));
        return results.OrderBy(x => ResourceRiskLevel.Rank(x.RiskLevel)).ThenBy(x => x.Name).ToList();
    }
}
