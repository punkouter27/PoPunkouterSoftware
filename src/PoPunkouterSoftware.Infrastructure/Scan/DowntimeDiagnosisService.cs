using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Diagnoses the root cause of downtime for broken or unreachable App Services
/// by inspecting ARM state, App Service Plan status, recent deployments, the
/// Azure Activity Log, App Insights exceptions, Kudu diagnostics, and GitHub
/// workflow run correlation.
/// </summary>
public class DowntimeDiagnosisService(
    ILogger<DowntimeDiagnosisService> logger,
    IHttpClientFactory httpClientFactory)
{
    private readonly ILogger<DowntimeDiagnosisService> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    internal async Task<List<ServiceDowntimeDiagnosis>> DiagnoseAsync(
        List<RawService> brokenServices,
        string subscriptionId,
        string? armToken,
        List<AppInsightsMetric>? appInsightsMetrics,
        Dictionary<string, GitHubWorkflowRun>? gitHubRuns,
        CancellationToken ct)
    {
        if (armToken is null)
            return [];

        var appInsightsByName = (appInsightsMetrics ?? [])
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Match App Insights components to services by fuzzy name matching
        var appInsightsByService = new Dictionary<string, AppInsightsMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in brokenServices)
        {
            if (appInsightsByName.TryGetValue(svc.Name, out var direct))
                appInsightsByService[svc.Name] = direct;
            else
            {
                var fuzzy = appInsightsByName.FirstOrDefault(kv => NameMatching.FuzzyContains(svc.Name, kv.Key));
                if (fuzzy.Key is not null)
                    appInsightsByService[svc.Name] = fuzzy.Value;
            }
        }

        var client = _httpClientFactory.CreateClient("azure-arm");
        using var gate = new SemaphoreSlim(4);

        var tasks = brokenServices.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var tok = cts.Token;

                string? availState = null, usageState = null, serverFarmId = null;
                bool isSuspended = false;
                DateTime? suspendedTill = null;
                string? planName = null, planStatus = null, planSku = null;
                bool planStopped = false;

                // 1 — Site ARM details
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{svc.ResourceId}?api-version=2023-12-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props))
                        {
                            availState = props.TryGetProperty("availabilityState", out var av) ? av.GetString() : null;
                            usageState = props.TryGetProperty("usageState", out var us) ? us.GetString() : null;
                            serverFarmId = props.TryGetProperty("serverFarmId", out var sfId) ? sfId.GetString() : null;
                            if (props.TryGetProperty("suspendedTill", out var susp) && susp.ValueKind == JsonValueKind.String)
                            {
                                if (DateTime.TryParse(susp.GetString(), out var suspDate))
                                {
                                    suspendedTill = suspDate;
                                    isSuspended = suspDate > DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Site ARM detail failed for {Name}", svc.Name); }

                // 2 — App Service Plan state
                if (serverFarmId is not null)
                {
                    try
                    {
                        planName = serverFarmId.Split('/').LastOrDefault();
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            $"https://management.azure.com{serverFarmId}?api-version=2023-12-01");
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                        using var resp = await client.SendAsync(req, tok);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync(tok);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("properties", out var props))
                                planStatus = props.TryGetProperty("status", out var st) ? st.GetString() : null;
                            if (doc.RootElement.TryGetProperty("sku", out var sku))
                                planSku = sku.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                            planStopped = planStatus is "Stopped";
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Plan ARM detail failed for {Name}", svc.Name); }
                }

                // 3 — Recent deployments (last 5)
                var deployments = new List<DeploymentEntry>();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{svc.ResourceId}/deployments?api-version=2023-12-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var arr))
                        {
                            foreach (var item in arr.EnumerateArray().Take(5))
                            {
                                if (!item.TryGetProperty("properties", out var p))
                                    continue;
                                int? statusCode = p.TryGetProperty("status", out var sc) && sc.ValueKind == JsonValueKind.Number
                                    ? sc.GetInt32() : null;
                                DateTime? depAt = p.TryGetProperty("end_time", out var et) && et.ValueKind == JsonValueKind.String
                                    && DateTime.TryParse(et.GetString(), out var dt) ? dt : null;
                                var statusText = statusCode switch
                                {
                                    4 => "Success",
                                    3 => "Failed",
                                    2 => "Deploying",
                                    1 => "Building",
                                    0 => "Pending",
                                    _ => statusCode?.ToString()
                                };
                                deployments.Add(new DeploymentEntry
                                {
                                    DeploymentId = item.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                                    Active = p.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
                                    StatusCode = statusCode,
                                    StatusText = statusText,
                                    Message = p.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                                        ? msg.GetString() : null,
                                    DeployedAt = depAt,
                                    Author = p.TryGetProperty("author_email", out var auth) && auth.ValueKind == JsonValueKind.String
                                        ? auth.GetString() : null,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Deployments fetch failed for {Name}", svc.Name); }

                // 4 — Activity log (last 48 hours)
                var activityLog = new List<ActivityLogEntry>();
                try
                {
                    var since = DateTime.UtcNow.AddHours(-48).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    var filter = Uri.EscapeDataString(
                        $"eventTimestamp ge '{since}' and resourceId eq '{svc.ResourceId}'");
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Insights/eventtypes/management/values?api-version=2015-04-01&$filter={filter}&$top=15");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var arr))
                        {
                            foreach (var ev in arr.EnumerateArray().Take(15))
                            {
                                var opName = ev.TryGetProperty("operationName", out var op)
                                    && op.TryGetProperty("localizedValue", out var lv) ? lv.GetString() : null;
                                var status = ev.TryGetProperty("status", out var st)
                                    && st.TryGetProperty("localizedValue", out var stv) ? stv.GetString() : null;
                                DateTime? ts = ev.TryGetProperty("eventTimestamp", out var tsProp)
                                    && tsProp.ValueKind == JsonValueKind.String
                                    && DateTime.TryParse(tsProp.GetString(), out var dt) ? dt : null;
                                var caller = ev.TryGetProperty("caller", out var cal) && cal.ValueKind == JsonValueKind.String
                                    ? cal.GetString() : null;
                                var level = ev.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.String
                                    ? lvl.GetString() : null;
                                activityLog.Add(new ActivityLogEntry
                                {
                                    OperationName = opName,
                                    Status = status,
                                    EventTimestamp = ts,
                                    Caller = caller,
                                    Level = level,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Activity log fetch failed for {Name}", svc.Name); }

                // 5 — Kudu SCM diagnostics: process list
                string? kuduProcesses = null;
                bool kuduReachable = false;
                try
                {
                    var scmUrl = $"https://{svc.Name}.scm.azurewebsites.net/api/processes";
                    using var kuduReq = new HttpRequestMessage(HttpMethod.Get, scmUrl);
                    kuduReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var kuduResp = await client.SendAsync(kuduReq, tok);
                    if (kuduResp.IsSuccessStatusCode)
                    {
                        kuduReachable = true;
                        var procJson = await kuduResp.Content.ReadAsStringAsync(tok);
                        using var procDoc = JsonDocument.Parse(procJson);
                        if (procDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var procs = procDoc.RootElement.EnumerateArray()
                                .Select(p => p.TryGetProperty("process_name", out var pn) ? pn.GetString() : null)
                                .Where(pn => pn is not null)
                                .Take(10)
                                .ToList();
                            kuduProcesses = procs.Count > 0
                                ? string.Join(", ", procs)
                                : "No processes running (crash loop or never started)";
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Kudu SCM fetch failed for {Name}", svc.Name); }

                // 5b — Azure Resource Health: the platform's own verdict on this resource, which is
                // the only signal here that separates "Azure is down under you" from "your app
                // is broken". Reader covers the read; a failure leaves it unknown, not false.
                var platform = await GetPlatformHealthAsync(client, svc.ResourceId!, armToken, tok);

                // 6 — App Insights exception correlation
                appInsightsByService.TryGetValue(svc.Name, out var aiMetrics);

                // 7 — GitHub workflow run correlation
                GitHubWorkflowRun? ghRun = null;
                gitHubRuns?.TryGetValue(svc.Name, out ghRun);

                // ── Determine most likely cause + suggested fix ──────────────
                string likelyCause;
                string? suggestedFix;

                if (usageState == "Exceeded" || isSuspended)
                {
                    likelyCause = "Free-tier CPU/memory quota exceeded — app is suspended until the next quota cycle";
                    suggestedFix = suspendedTill.HasValue
                        ? $"App will auto-resume around {suspendedTill.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Upgrade to B1 to prevent recurrence."
                        : "Upgrade to B1 plan or wait for the daily quota reset (midnight UTC).";
                }
                else if (planStopped)
                {
                    likelyCause = $"App Service Plan '{planName ?? "unknown"}' is stopped — all apps on this plan are down";
                    suggestedFix = $"az appservice plan update --name \"{planName}\" --resource-group \"{svc.ResourceGroup}\" --status Running";
                }
                else if (svc.PlatformState == "Stopped")
                {
                    likelyCause = "App Service is stopped";
                    suggestedFix = $"az webapp start --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\"";
                }
                else if (platform.Caused == true)
                {
                    likelyCause = $"Azure platform event — Resource Health reports {platform.State}"
                        + (platform.Summary is { Length: > 0 } s ? $": {s}" : "");
                    suggestedFix = "Nothing to fix in the app. Track the incident in Azure Service Health; the app recovers when the platform does.";
                }
                else if (deployments.Any(d => d.StatusCode == 3))
                {
                    var failed = deployments.First(d => d.StatusCode == 3);
                    likelyCause = "Last deployment failed — app may be running a broken build or unable to start";
                    var msg = failed.Message;
                    suggestedFix = msg is not null && msg.Length > 0
                        ? (msg.Length > 250 ? msg[..250] + "…" : msg)
                        : "Check Kudu deployment logs for the error details.";
                }
                else if (ghRun?.Conclusion == "failure")
                {
                    likelyCause = $"Recent GitHub Actions workflow run failed — deployment may not have reached Azure";
                    suggestedFix = ghRun.RunUrl is not null
                        ? $"Review the failed workflow run: {ghRun.RunUrl}"
                        : "Check GitHub Actions for the latest run details.";
                }
                else if (availState is not null && availState != "Normal")
                {
                    likelyCause = $"Azure platform reports availability state: {availState}";
                    suggestedFix = "Check Azure Service Health for ongoing incidents in your region.";
                }
                else if (activityLog.Any(a => a.OperationName?.Contains("Stop", StringComparison.OrdinalIgnoreCase) == true
                    && a.Status?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var stopEvent = activityLog.First(a => a.OperationName!.Contains("Stop", StringComparison.OrdinalIgnoreCase));
                    likelyCause = $"App was recently stopped via management API"
                        + (stopEvent.Caller is not null ? $" by {stopEvent.Caller}" : "")
                        + (stopEvent.EventTimestamp.HasValue ? $" at {stopEvent.EventTimestamp.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "");
                    suggestedFix = $"az webapp start --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\"";
                }
                else if (aiMetrics is { Exceptions7Days: > 0 })
                {
                    likelyCause = $"Application error — {aiMetrics.Exceptions7Days} exceptions and {aiMetrics.FailedRequests7Days ?? 0} failed requests in 7 days"
                        + (kuduProcesses is not null ? $" (Kudu processes: {kuduProcesses})" : "");
                    suggestedFix = $"Investigate the top exception types in Application Insights for '{aiMetrics.Name}'. "
                        + "Common causes: connection string issues, missing Key Vault secrets, startup race conditions, or unhandled exceptions.";
                }
                else if (svc.HttpStatus == "unreachable")
                {
                    likelyCause = "App is unreachable — TCP connection failed or DNS not resolving";
                    suggestedFix = "Verify the App Service hostname in the portal and check if the app is Running. Test with: curl -I " + (svc.Url ?? "https://<app>.azurewebsites.net");
                }
                else
                {
                    var extraInfo = "";
                    if (kuduProcesses is not null)
                        extraInfo = $" Kudu processes: {kuduProcesses}.";
                    if (aiMetrics is { Exceptions7Days: 0, FailedRequests7Days: 0 })
                        extraInfo += " No App Insights exceptions. Kudu is reachable — check startup logs at the SCM site.";

                    likelyCause = "No obvious infrastructure cause found — likely an application-level error (crash loop, bad startup config, or missing secrets)" + extraInfo;
                    suggestedFix = kuduReachable
                        ? $"Check Kudu logs at: https://{svc.Name}.scm.azurewebsites.net/api/logstream"
                        : $"Check Application Insights for exceptions or stream live logs at: https://{svc.Name}.scm.azurewebsites.net/api/logstream";
                }

                return new ServiceDowntimeDiagnosis
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    HttpStatus = svc.HttpStatus,
                    AvailabilityState = availState,
                    UsageState = usageState,
                    IsSuspended = isSuspended,
                    SuspendedTill = suspendedTill,
                    PlanName = planName,
                    PlanStatus = planStatus,
                    PlanSku = planSku,
                    PlanStopped = planStopped,
                    RecentDeployments = deployments,
                    RecentActivity = activityLog,
                    LikelyCause = likelyCause,
                    SuggestedFix = suggestedFix,
                    AppInsightsExceptions7Days = aiMetrics?.Exceptions7Days,
                    AppInsightsFailedRequests7Days = aiMetrics?.FailedRequests7Days,
                    GitHubWorkflowRunUrl = ghRun?.RunUrl,
                    GitHubWorkflowStatus = ghRun?.Status,
                    GitHubWorkflowConclusion = ghRun?.Conclusion,
                    GitHubWorkflowCompletedAt = ghRun?.CompletedAt,
                    KuduProcesses = kuduProcesses,
                    KuduReachable = kuduReachable,
                    PlatformHealth = platform.State,
                    PlatformHealthSummary = platform.Summary,
                    PlatformCaused = platform.Caused,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downtime diagnosis failed for {Name}", svc.Name);
                return new ServiceDowntimeDiagnosis
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    HttpStatus = svc.HttpStatus,
                    LikelyCause = "Diagnosis step failed — see application logs",
                    SuggestedFix = ex.Message,
                };
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>
    /// Resource Health's current status for one resource. <c>Caused</c> is true when Azure
    /// itself says the resource is down for a platform reason (planned maintenance or an
    /// unplanned platform fault), false when Azure says it is available or that a user action
    /// caused the state, and null when Resource Health did not answer.
    /// </summary>
    private async Task<(string? State, string? Summary, bool? Caused)> GetPlatformHealthAsync(
        HttpClient client, string resourceId, string armToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://management.azure.com{resourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version=2022-10-01");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return ReadPlatformHealth(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Resource Health lookup failed for {ResourceId}", resourceId);
            return (null, null, null);
        }
    }

    /// <summary>Pure: one availabilityStatuses/current document to the verdict above.</summary>
    public static (string? State, string? Summary, bool? Caused) ReadPlatformHealth(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var p))
            return (null, null, null);

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var state = Str(p, "availabilityState");
        var reason = Str(p, "reasonType");
        bool? caused = state switch
        {
            null or "Unknown" => null,
            "Available" => false,
            _ => reason is "Unplanned" or "Planned",
        };
        return (state, Str(p, "summary"), caused);
    }
}

/// <summary>
/// Per-service GitHub workflow run correlation data, produced by
/// the CI/CD review endpoint and consumed by the downtime diagnosis.
/// </summary>
public record GitHubWorkflowRun
{
    public string ServiceName { get; init; } = "";
    public string? RunUrl { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? RunName { get; init; }
}
