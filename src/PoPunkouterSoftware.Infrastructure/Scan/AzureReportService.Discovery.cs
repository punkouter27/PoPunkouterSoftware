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
/// Steps 1-3: discover web services, resolve their App Service Plans, probe reachability, and load the raw ARM resource set.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<RawService>> DiscoverWebServicesAsync(SubscriptionResource sub, CancellationToken ct)
    {
        var list = new List<RawService>();

        // App Service web apps
        await foreach (var site in sub.GetWebSitesAsync(cancellationToken: ct))
        {
            if (site.Data.Name.Contains('/'))
                continue; // skip slots
            var url = site.Data.DefaultHostName is { } h ? $"https://{h}" : null;
            var rg = site.Data.Id?.ResourceGroupName ?? "";
            var isFunctionApp = site.Data.Kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new RawService
            {
                Name = site.Data.Name,
                FriendlyName = FriendlyFromContext(site.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = isFunctionApp ? "Microsoft.Web/sites/functions" : "Microsoft.Web/sites",
                Kind = site.Data.Kind,
                Url = url,
                Sku = null, // SKU is on the App Service Plan, not the site
                PlatformState = site.Data.State,
                ResourceId = site.Data.Id?.ToString(),
                Region = ArmRegion(site.Data.Location.Name),
                CustomHosts = (site.Data.HostNames ?? []).Where(IsCustomHost).ToList(),
            });
        }

        // Static Web Apps
        await foreach (var swa in sub.GetStaticSitesAsync(cancellationToken: ct))
        {
            var url = swa.Data.DefaultHostname is { } h ? $"https://{h}" : null;
            var rg = swa.Data.Id?.ResourceGroupName ?? "";
            list.Add(new RawService
            {
                Name = swa.Data.Name,
                FriendlyName = FriendlyFromContext(swa.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = "Microsoft.Web/staticSites",
                Url = url,
                Sku = swa.Data.Sku?.Name ?? "Free",
                PlatformState = "Running",
                ResourceId = swa.Data.Id?.ToString(),
                Region = ArmRegion(swa.Data.Location.Name),
                CustomHosts = (swa.Data.CustomDomains ?? []).Where(IsCustomHost).ToList(),
            });
        }

        // Container Apps (via generic ARM filter)
        await foreach (var ca in sub.GetGenericResourcesAsync(
            filter: "resourceType eq 'Microsoft.App/containerApps'",
            cancellationToken: ct))
        {
            var rg = ca.Data.Id?.ResourceGroupName ?? "";
            list.Add(new RawService
            {
                Name = ca.Data.Name,
                FriendlyName = FriendlyFromContext(ca.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = "Microsoft.App/containerApps",
                Url = null,
                Sku = "Consumption",
                PlatformState = "Running",
                ResourceId = ca.Data.Id?.ToString(),
            });
        }

        return list;
    }

    /// <summary>ARM hands back "West US 2" for a site; the Retail Prices API wants "westus2".</summary>
    private static string ArmRegion(string? location) =>
        (location ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>A host name the owner registered, not one Azure hands out.</summary>
    private static bool IsCustomHost(string host) =>
        !string.IsNullOrWhiteSpace(host)
        && !host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".azurestaticapps.net", StringComparison.OrdinalIgnoreCase);

    private async Task<List<RawService>> ResolveAppServicePlansAsync(
        List<RawService> services, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return services;

        var sites = services
            .Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null)
            .ToList();

        if (sites.Count == 0)
            return services;

        var client = _httpClientFactory.CreateClient("azure-arm");
        var planSkus = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(6);
        var tasks = sites.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{svc.ResourceId}?api-version=2023-12-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                using var resp = await client.SendAsync(req, cts.Token);

                if (!resp.IsSuccessStatusCode)
                    return ((RawService?)null, (string?)null, (string?)null);

                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    return ((RawService?)null, (string?)null, (string?)null);

                var serverFarmId = props.TryGetProperty("serverFarmId", out var sfId) ? sfId.GetString() : null;
                if (serverFarmId is null)
                    return ((RawService?)null, (string?)null, (string?)null);

                var planName = serverFarmId.Split('/').LastOrDefault();
                if (planName is null)
                    return ((RawService?)null, (string?)null, (string?)null);

                string? planSku = null;
                if (!planSkus.TryGetValue(serverFarmId, out planSku))
                {
                    try
                    {
                        using var planReq = new HttpRequestMessage(HttpMethod.Get,
                            $"https://management.azure.com{serverFarmId}?api-version=2023-12-01");
                        planReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                        using var planResp = await client.SendAsync(planReq, cts.Token);

                        if (planResp.IsSuccessStatusCode)
                        {
                            var planJson = await planResp.Content.ReadAsStringAsync(cts.Token);
                            using var planDoc = JsonDocument.Parse(planJson);
                            if (planDoc.RootElement.TryGetProperty("sku", out var sku))
                                planSku = sku.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                        }
                    }
                    catch { /* plan fetch failed — leave SKU null */ }

                    planSkus[serverFarmId] = planSku ?? "";
                }

                return (svc, planName, planSku);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Plan resolution failed for {Name}", svc.Name);
                return ((RawService?)null, (string?)null, (string?)null);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var (svc, planName, planSku) in results)
        {
            if (svc is null || planName is null)
                continue;

            var idx = services.FindIndex(s => s.ResourceId == svc.ResourceId);
            if (idx >= 0)
                services[idx] = services[idx] with { AppServicePlan = planName, AppServicePlanSku = planSku };
        }

        return services;
    }

    private async Task<List<RawService>> TestConnectivityAsync(List<RawService> services, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("azure-probe");
        var tasks = services.Select(async svc =>
        {
            if (string.IsNullOrEmpty(svc.Url))
                return svc with { Connectivity = new ConnectivityInfo { Success = false, Error = "No URL" }, HttpStatus = "unknown" };
            var conn = await ProbeUrlAsync(client, svc.Url, ct);
            var status = conn.Success ? "active" : conn.ResponseTime > 0 ? "broken" : "unreachable";
            return svc with { Connectivity = conn, HttpStatus = status };
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<ConnectivityInfo> ProbeUrlAsync(HttpClient client, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // GET, headers only — the same request ServicePingerService sends. This was HEAD, and
            // an ASP.NET app with no HEAD route answers 405: this site's own production instance
            // was ranked "FIX NOW" on its own dashboard while serving every GET with a 200.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var isAzureError = resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway;
            // A 3xx counts as up. The client does not follow redirects (by design — following
            // them turns "this app is alive" into "some other host answered"), so an app that
            // redirects "/" to a sign-in page answers 302 here. Judging on IsSuccessStatusCode
            // alone marked PoRedoImage and PoRepoLineTracker "unavailable" while both were
            // serving normally, and disagreed with ServicePingerService — the other writer into
            // the uptime partition — which has always counted anything under 500 as reachable.
            var reachable = (int)resp.StatusCode < 400;
            return new ConnectivityInfo
            {
                Success = reachable && !isAzureError,
                ResponseTime = (int)sw.ElapsedMilliseconds,
                Error = isAzureError ? "Azure error page" : null,
                IsAzureErrorPage = isAzureError,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectivityInfo { Success = false, ResponseTime = (int)sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    private static async Task<List<GenericResourceData>> GetAllResourcesAsync(SubscriptionResource sub, CancellationToken ct)
    {
        var list = new List<GenericResourceData>();
        await foreach (var r in sub.GetGenericResourcesAsync(cancellationToken: ct))
            list.Add(r.Data);
        return list;
    }
}
