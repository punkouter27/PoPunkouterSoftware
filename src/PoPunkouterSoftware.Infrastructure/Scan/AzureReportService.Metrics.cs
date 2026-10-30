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
/// Steps 4 and 14: per-service Azure Monitor metrics over 7 days, and Application Insights component metrics.
/// </summary>
public partial class AzureReportService
{
    private async Task<Dictionary<string, MetricsInfo>> GetMetricsAsync(
        List<RawService> services, TokenCredential cred, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, MetricsInfo>(StringComparer.OrdinalIgnoreCase);
        var appSvcs = services.Where(s => s.ResourceId is not null && s.ResourceTypeRaw == "Microsoft.Web/sites").ToList();
        if (appSvcs.Count == 0)
            return [];

        MetricsQueryClient metricsClient;
        try
        { metricsClient = new MetricsQueryClient(cred); }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "MetricsQueryClient could not be initialised — skipping metrics (expected in local dev without App Insights)");
            return [];
        }

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);

        using var gate = new SemaphoreSlim(6);
        var tasks = appSvcs.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var response = await metricsClient.QueryResourceAsync(
                    svc.ResourceId!,
                    ["Requests", "Http5Xx", "AverageResponseTime"],
                    new MetricsQueryOptions { TimeRange = new QueryTimeRange(start, end), Granularity = TimeSpan.FromDays(1) },
                    ct);

                int requests = 0, http5xx = 0;
                double avgRt = 0;
                foreach (var metric in response.Value.Metrics)
                {
                    var total = metric.TimeSeries.SelectMany(ts => ts.Values)
                        .Sum(p => p.Total ?? p.Average ?? 0);
                    if (metric.Name.Contains("Request", StringComparison.OrdinalIgnoreCase) && !metric.Name.Contains("5"))
                        requests = (int)total;
                    else if (metric.Name.Contains("5", StringComparison.OrdinalIgnoreCase))
                        http5xx = (int)total;
                    else if (metric.Name.Contains("Response", StringComparison.OrdinalIgnoreCase))
                        avgRt = Math.Round(total, 1);
                }

                result[svc.ResourceId!] = new MetricsInfo
                {
                    Requests = requests,
                    Http5xx = http5xx,
                    AverageResponseTime = avgRt,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metrics unavailable for {Name}", svc.Name);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Retrieves request rates, exception rates, and performance metrics from Application Insights.</summary>
    private async Task<List<AppInsightsMetric>> GetAppInsightsMetricsAsync(
        List<GenericResourceData> allResources, TokenCredential cred, CancellationToken ct)
    {
        var components = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "microsoft.insights/components", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (components.Count == 0)
            return [];

        // App Insights telemetry (requests, exceptions) lives in Log Analytics —
        // it cannot be queried via MetricsQueryClient. Use LogsQueryClient instead.
        LogsQueryClient logsClient;
        try
        { logsClient = new LogsQueryClient(cred); }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "LogsQueryClient unavailable for App Insights — expected in local dev without connection string");
            return [];
        }

        var timeRange = new QueryTimeRange(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

        using var gate = new SemaphoreSlim(4);
        var tasks = components.Select(async comp =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var resourceId = new ResourceIdentifier(comp.Id!.ToString());

                int requests = 0, failed = 0, exceptions = 0;

                // Requests + failures
                var reqResp = await logsClient.QueryResourceAsync(
                    resourceId,
                    "requests | summarize totalCount=count(), failedCount=countif(success==false)",
                    timeRange,
                    cancellationToken: ct);
                if (reqResp.Value?.Table?.Rows is { Count: > 0 } reqRows)
                {
                    requests = (int)(reqRows[0].GetInt64(0) ?? 0L);
                    failed = (int)(reqRows[0].GetInt64(1) ?? 0L);
                }

                // Exceptions
                var excResp = await logsClient.QueryResourceAsync(
                    resourceId,
                    "exceptions | summarize exCount=count()",
                    timeRange,
                    cancellationToken: ct);
                if (excResp.Value?.Table?.Rows is { Count: > 0 } excRows)
                    exceptions = (int)(excRows[0].GetInt64(0) ?? 0L);

                return new AppInsightsMetric
                {
                    Name = comp.Name,
                    ResourceGroup = comp.Id?.ResourceGroupName,
                    Requests7Days = requests,
                    FailedRequests7Days = failed,
                    Exceptions7Days = exceptions,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "App Insights logs query failed for {Name}", comp.Name);
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.OfType<AppInsightsMetric>().OrderBy(x => x.Name).ToList();
    }
}
