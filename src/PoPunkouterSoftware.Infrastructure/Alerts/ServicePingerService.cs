using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Background service that periodically pings each Azure web service URL to warm cold-start
/// instances. Its output is telemetry, not a queryable snapshot: each ping records the
/// <c>pinger_service_up</c> gauge and <c>pinger_response_time_ms</c> histogram, and each
/// completed sweep bumps <c>pinger_sweeps_total</c> as a liveness heartbeat.
///
/// <para>The results were previously also cached in IMemoryCache to back a
/// <c>/api/pinger/status</c> endpoint, along with a per-service toggle. No UI ever read
/// either, so the cache, the snapshot accessor and the toggle were removed — the metrics
/// are the product.</para>
/// </summary>
public sealed partial class ServicePingerService : BackgroundService
{
    // Source-generated logging — zero boxing/allocation on the per-ping hot path.
    [LoggerMessage(Level = LogLevel.Information, Message = "Pinger disabled by configuration.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pinger: {Name} -> {Status} ({Ms} ms)")]
    private partial void LogPing(string name, string status, long ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pinger sweep complete: {Total} services probed")]
    private partial void LogSweepComplete(int total);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlertNotifier _alerts;
    private readonly OutageTracker _outages = new();
    private readonly ILogger<ServicePingerService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly int _maxConcurrency;
    private readonly int _retentionDays;

    public ServicePingerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        AlertNotifier alerts,
        IConfiguration config,
        ILogger<ServicePingerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _alerts = alerts;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(config.GetValue<int>("Pinger:IntervalMinutes", 10));
        _enabled = config.GetValue("Pinger:Enabled", true);
        _maxConcurrency = Math.Clamp(config.GetValue("Pinger:MaxConcurrency", 4), 1, 12);
        // Uptime samples age out on the same schedule as history rows, so the grid's two data
        // sources cannot disagree about how far back "the last 30 days" reaches.
        _retentionDays = config.GetValue("Retention:HistoryDays", 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogDisabled();
            return;
        }

        try
        {
            // Delay 30 s on startup so the app fully initialises before the first sweep.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PingAllServicesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw; // graceful shutdown — handled by the outer catch
                }
                catch (Exception ex)
                {
                    // A single failed sweep must never escape ExecuteAsync: the default
                    // BackgroundServiceExceptionBehavior is StopHost, so an unguarded throw
                    // here would take down the whole web app (or silently end pinging).
                    _logger.LogError(ex, "Pinger sweep failed — retrying on the next interval");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal graceful shutdown — host cancellation token fired. Do not rethrow.
        }
    }

    private async Task PingAllServicesAsync(CancellationToken ct)
    {
        // Load the latest report to get the current service list.
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<AzureReportStore>();
        var reportResult = await repository.LoadAsync(ct);
        if (!reportResult.IsSuccess || reportResult.Value?.WebServices is null)
            return;

        var services = reportResult.Value.WebServices.Services;
        var client = _httpClientFactory.CreateClient("azure-probe");

        using var gate = new SemaphoreSlim(_maxConcurrency);
        var tasks = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await PingOneAsync(client, svc.Name, svc.FriendlyName, svc.Url!, ct);
                LogPing(svc.Name, result.Status, result.ResponseTimeMs);

                // Metrics: per-service up/down gauge + response-time histogram so reachability
                // and latency trends are alertable without parsing logs. (questions 4 & 6)
                var serviceTag = new KeyValuePair<string, object?>("service", svc.Name);
                Telemetry.PingerServiceUp.Record(result.Status == "reachable" ? 1 : 0, serviceTag);
                Telemetry.PingerResponseTime.Record(result.ResponseTimeMs, serviceTag);
                return result;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        // Persist the sweep as a per-day tally so the /azure uptime grid has data on days when
        // no full Azure scan ran — scans only happen on a manual rescan or a stale-portfolio
        // page load, so an untrafficked day would otherwise be a blank column. Best-effort:
        // this loop exists to warm cold-start instances, and a Table Storage blip must not
        // stop it doing that.
        //
        // "reachable" is healthy; "degraded" (5xx) and "unreachable" (DNS/socket) are not.
        //
        // "timeout" is deliberately NOT recorded either way. These are F1 apps that sleep when
        // idle, and a 14-second probe that misses a cold start is not evidence the app is down
        // — the dashboard says as much under the response-time chart. Counting it as downtime
        // would paint the grid red every night; counting it as uptime would be a lie. It is
        // simply not an observation, the same way a day with no scan is not a healthy day.
        try
        {
            var samples = scope.ServiceProvider.GetRequiredService<UptimeSampleStore>();
            var now = DateTimeOffset.UtcNow;
            await samples.RecordSweepAsync(
                results
                    .Where(r => r.Status != "timeout")
                    .Select(r => (
                        ServiceName: NameMatching.ServiceIdentity(r.FriendlyName, r.Name),
                        Healthy: r.Status == "reachable")),
                now, ct);

            // No scheduler exists to hang a cleanup job on, so pruning rides the write path —
            // the same approach AzureReportStore takes for history rows.
            if (now - _lastPruneAt > PruneEvery)
            {
                _lastPruneAt = now;
                await samples.PruneAsync(_retentionDays, now, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist uptime samples for this sweep (non-fatal)");
        }

        await NotifyTransitionsAsync(results, ct);

        // Heartbeat: a flat sweep-counter rate means the background loop has silently died. (question 5)
        Telemetry.PingerSweeps.Add(1);
        LogSweepComplete(results.Length);
    }

    /// <summary>
    /// Phone alert on each debounced up→down or down→up transition. Fed the same observations
    /// the uptime grid records — timeouts are no observation — so a notification can never
    /// disagree with the grid it links to.
    /// </summary>
    private async Task NotifyTransitionsAsync(PingResult[] results, CancellationToken ct)
    {
        foreach (var r in results)
        {
            var name = NameMatching.ServiceIdentity(r.FriendlyName, r.Name);
            var transition = _outages.Observe(name, r.Status == "timeout" ? null : r.Status == "reachable");
            if (transition is null)
                continue;

            var focus = $"/azure?focus={Uri.EscapeDataString(name)}";
            if (transition == OutageTransition.WentDown)
                await _alerts.SendAsync(
                    $"{name} is down",
                    $"{r.Status} on {_outages.FailuresBeforeAlert} checks in a row{(r.Error is null ? "" : $": {r.Error}")}\n{r.Url}",
                    priority: 4, clickPath: focus, tags: ["rotating_light"], ct: ct);
            else
                await _alerts.SendAsync(
                    $"{name} is back",
                    $"Reachable again ({r.ResponseTimeMs} ms)\n{r.Url}",
                    priority: 3, clickPath: focus, tags: ["white_check_mark"], ct: ct);
        }
    }

    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private static async Task<PingResult> PingOneAsync(
        HttpClient client, string name, string friendlyName, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(14));
            var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            var status = (int)resp.StatusCode < 500 ? "reachable" : "degraded";
            return new PingResult(name, friendlyName, url, status, sw.ElapsedMilliseconds, null, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new PingResult(name, friendlyName, url, "timeout", sw.ElapsedMilliseconds, "Request timed out", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PingResult(name, friendlyName, url, "unreachable", sw.ElapsedMilliseconds, ex.Message, DateTime.UtcNow);
        }
    }

}

public sealed record PingResult(
    string Name,
    string FriendlyName,
    string Url,
    string Status,
    long ResponseTimeMs,
    string? Error,
    DateTime PingedAt);
