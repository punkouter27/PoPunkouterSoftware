using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

/// <summary>
/// Owns the full Azure inventory refresh run (scan → save → file cache →
/// screenshots → SignalR progress). One shared entry point so both the manual
/// POST /api/diag/refresh and the automatic staleness-triggered rescan execute the
/// identical pipeline under the same <see cref="RefreshSessionManager"/> lock.
/// Register as a singleton.
/// </summary>
internal sealed class ReportRefreshRunner(
    IServiceScopeFactory scopeFactory,
    IWebHostEnvironment env,
    ILogger<ReportRefreshRunner> logger,
    IHubContext<RefreshHub> hubCtx,
    RefreshSessionManager session,
    AiTriageService aiTriage,
    AlertNotifier alerts,
    IConfiguration config)
{
    /// <summary>
    /// Auto-triggered runs must not retry-storm when scans keep failing (bad credentials,
    /// ARM throttling): at most one automatic attempt per cooldown window. Manual runs
    /// are never throttled. In-memory only — resets on restart, which is fine.
    /// </summary>
    private static readonly TimeSpan AutoAttemptCooldown = TimeSpan.FromMinutes(30);
    private readonly object _autoGate = new();
    private DateTimeOffset _lastAutoAttemptUtc = DateTimeOffset.MinValue;

    // Hoisted so System.Text.Json's converter metadata cache is reused instead of being
    // rebuilt for every refresh run that writes the file cache.
    private static readonly JsonSerializerOptions FileCacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>True while a refresh run holds the session lock.</summary>
    public bool IsRunning => session.Lock.CurrentCount == 0;

    /// <summary>
    /// Starts a background refresh for the stale-inventory path. Returns false when one is
    /// already running or an automatic attempt happened within the cooldown window.
    /// </summary>
    public bool TryStartAuto()
    {
        lock (_autoGate)
        {
            if (DateTimeOffset.UtcNow - _lastAutoAttemptUtc < AutoAttemptCooldown)
                return false;
            if (IsRunning)
                return false;
            _lastAutoAttemptUtc = DateTimeOffset.UtcNow;
        }
        return TryStart("auto-stale");
    }

    /// <summary>
    /// Starts the refresh pipeline in the background. Returns false (recording a collision
    /// sample) when another run already holds the lock. Fire-and-forget by design: progress
    /// and the terminal outcome are broadcast over the RefreshHub.
    /// </summary>
    public bool TryStart(string trigger)
    {
        // Records one refresh-run sample tagged by outcome (success|failed|cancelled|collision).
        static void RecordOutcome(string outcome) =>
            Telemetry.RefreshRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        if (!session.Lock.Wait(0))
        {
            RecordOutcome("collision");
            logger.LogInformation("Refresh rejected — another refresh is already in progress (trigger: {Trigger}).", trigger);
            return false;
        }

        _ = Task.Run(async () =>
        {
            // The outer try/finally is the only owner of the semaphore: no statement that
            // can throw (scope creation, DI resolution, CTS allocation) runs outside it,
            // so a fault can never leak the lock and dead-lock every future refresh.
            string? terminalError = null;
            var refreshRunId = Guid.NewGuid();
            try
            {
                // Root span so the hundreds of outbound ARM/GitHub dependency calls made by
                // this background run are parented and correlatable in Azure Monitor.
                using var activity = Telemetry.Source.StartActivity("refresh");
                activity?.SetTag("refresh.run_id", refreshRunId);
                activity?.SetTag("refresh.trigger", trigger);
                using var _logScope = Serilog.Context.LogContext.PushProperty("RefreshRunId", refreshRunId);

                using var scope = scopeFactory.CreateScope();
                var azureService = scope.ServiceProvider.GetRequiredService<AzureReportService>();
                var store = scope.ServiceProvider.GetRequiredService<AzureReportStore>();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                session.SetActiveCts(cts);
                try
                {
                    var ct = cts.Token;
                    var sw = Stopwatch.StartNew();
                    var progress = new Progress<(string Step, int Percent)>(p =>
                    {
                        _ = hubCtx.Clients.All.SendAsync("RefreshProgress",
                            new { step = p.Step, percent = p.Percent, done = false });
                    });

                    // Capture the outgoing "latest" report before it is overwritten so incident
                    // detection can diff against it without re-downloading history blobs.
                    var previousResult = await store.LoadAsync(ct);
                    var previousReport = previousResult.IsSuccess ? previousResult.Value : null;

                    var report = await azureService.RunAsync(progress, ct);

                    // Status-narrative precompute: assemble every figure the scan produced
                    // (StatusFactsBuilder — the same projection the read path uses, so the
                    // paragraph can never contradict the tiles beside it) and attach the
                    // result to the report before it is saved, so it persists through the
                    // existing store/file-cache paths with no new plumbing.
                    //
                    // GenerateSummaryAsync reuses the previous scan's paragraph (Source
                    // "cached") instead of calling the model when nothing material changed,
                    // and never throws for an AI outage — it always returns a populated
                    // result, falling back to the rule-based narrative.
                    //
                    // History is loaded here purely so the narrator can mention what moved
                    // since the previous scan; a failure to load it degrades the paragraph,
                    // never the scan.
                    var historyForFacts = new List<HistorySummary>();
                    try
                    {
                        var historyResult = await store.LoadHistorySummariesAsync(maxEntries: 30, ct);
                        if (historyResult.IsSuccess && historyResult.Value is not null)
                            historyForFacts = historyResult.Value;
                    }
                    catch (Exception hex)
                    {
                        logger.LogWarning(hex, "History load for the status narrative failed (non-fatal)");
                    }

                    var facts = StatusFactsBuilder.Build(
                        report,
                        historyForFacts,
                        config.GetValue<double?>("Budget:MonthlyUsd"),
                        PortfolioIdentity.IsSelf);
                    var aiSummary = await aiTriage.GenerateSummaryAsync(facts, previousReport?.AiSummary, ct);
                    report = report with { AiSummary = aiSummary };

                    await store.SaveAsync(report, ct);
                    await NotifyNewCostSpikesAsync(report, previousReport);
                    await NotifyCrossingsAsync(report, previousReport);

                    var json = JsonSerializer.Serialize(report, FileCacheJsonOptions);
                    var filePath = ReportFileCache.EnsureReportPath(env);
                    await File.WriteAllTextAsync(filePath, json, ct);

                    // Refresh the home-page screenshots alongside the inventory (non-fatal).
                    try
                    {
                        await hubCtx.Clients.All.SendAsync("RefreshProgress",
                            new { step = "Capturing app screenshots…", percent = 99, done = false }, CancellationToken.None);
                        var screenshots = scope.ServiceProvider.GetRequiredService<AppScreenshotService>();
                        // Scan-derived targets first; catalog fills the gap so a dev
                        // environment with no live Azure report still produces previews.
                        var captureTargets = AppScreenshotService.CombinedTargets(env, report);
                        await screenshots.CaptureAsync(captureTargets, ct);
                    }
                    catch (Exception shotEx)
                    {
                        logger.LogWarning(shotEx, "App screenshot capture failed (non-fatal)");
                    }

                    sw.Stop();
                    RecordOutcome("success");
                    Telemetry.RefreshDuration.Record(sw.Elapsed.TotalMilliseconds);
                    logger.LogInformation("Azure report refreshed in {ElapsedMs}ms (trigger: {Trigger})", sw.ElapsedMilliseconds, trigger);
                }
                finally
                {
                    // Clear the shared reference BEFORE the using disposes the CTS so a
                    // concurrent /cancel-refresh cannot cancel a disposed source.
                    session.SetActiveCts(null);
                }
            }
            catch (OperationCanceledException)
            {
                terminalError = "Refresh cancelled or timed out.";
                RecordOutcome("cancelled");
                logger.LogWarning("Refresh cancelled or timed out");
            }
            catch (Exception ex)
            {
                terminalError = "Refresh failed. Check server logs for details.";
                RecordOutcome("failed");
                logger.LogError(ex, "Azure report refresh failed: {Message}", ex.Message);
                // A failed scan leaves the dashboard showing yesterday as if it were today;
                // the nightly workflow's week of silent 403s is why this is a push, not a log.
                await alerts.SendAsync("Azure scan failed", $"{trigger} scan: {ex.Message}",
                    priority: 4, clickPath: "/azure", tags: ["warning"], ct: CancellationToken.None);
            }
            finally
            {
                session.Lock.Release();
                try
                {
                    await hubCtx.Clients.All.SendAsync("RefreshProgress",
                        new { step = terminalError is null ? "Done" : "Failed", percent = 100, done = true, error = terminalError },
                        CancellationToken.None);
                }
                catch (Exception hubEx)
                {
                    logger.LogWarning(hubEx, "Failed to broadcast terminal refresh status");
                }
            }
        });

        return true;
    }

    /// <summary>
    /// Certificate expiry, budget overrun, a page losing its expected content and a domain
    /// nearing expiry, each sent once when it is crossed — see
    /// <see cref="AlertTriggers"/> for why a crossing and not a state.
    /// </summary>
    private async Task NotifyCrossingsAsync(AzureReport report, AzureReport? previous)
    {
        foreach (var ssl in AlertTriggers.NewSslExpiries(report, previous))
        {
            await alerts.SendAsync(
                $"Certificate for {ssl.Name} expires in {ssl.DaysLeft} day{(ssl.DaysLeft == 1 ? "" : "s")}",
                $"{ssl.Url ?? ssl.Name} — expiry {ssl.Expiry ?? "unknown"}",
                priority: ssl.DaysLeft <= 3 ? 5 : 4, clickPath: "/azure", tags: ["lock"], ct: CancellationToken.None);
        }

        foreach (var svc in AlertTriggers.NewContentFailures(report, previous))
        {
            var name = string.IsNullOrWhiteSpace(svc.FriendlyName) ? svc.Name : svc.FriendlyName;
            await alerts.SendAsync(
                $"{name} is up but its page looks wrong",
                $"{svc.Url} answered without \"{svc.ContentCheck!.Expect}\"",
                priority: 4, clickPath: $"/azure?focus={Uri.EscapeDataString(name)}", tags: ["mag"], ct: CancellationToken.None);
        }

        foreach (var domain in AlertTriggers.NewDomainExpiries(report, previous, DateTime.UtcNow))
        {
            await alerts.SendAsync(
                $"Domain {domain.Domain} expires {domain.ExpiresAt:MMM d}",
                "Renew the registration before the certificate and every link on it stop working.",
                priority: 4, clickPath: "/azure", tags: ["globe_with_meridians"], ct: CancellationToken.None);
        }

        if (AlertTriggers.BudgetCrossed(report, previous, config.GetValue<double?>("Budget:MonthlyUsd")) is { } forecast)
        {
            await alerts.SendAsync(
                "Azure spend is on pace to exceed budget",
                $"Projected ${forecast.ProjectedMonthTotal:F2} against a ${forecast.BudgetUsd:F2} budget ({forecast.PercentOfBudget}%)",
                priority: 4, clickPath: "/azure", tags: ["money_with_wings"], ct: CancellationToken.None);
        }
    }

    /// <summary>
    /// One alert per spike, not per scan: anomalies the previous report already carried (same
    /// resource group, same day) were announced when that report landed.
    /// </summary>
    private async Task NotifyNewCostSpikesAsync(AzureReport report, AzureReport? previous)
    {
        var known = DashboardInsightsBuilder.BuildCostAnomalies(previous?.Cost?.ResourceGroups)
            .Select(a => (a.ResourceGroup.ToLowerInvariant(), a.Day))
            .ToHashSet();

        foreach (var spike in DashboardInsightsBuilder.BuildCostAnomalies(report.Cost?.ResourceGroups)
                     .Where(a => !known.Contains((a.ResourceGroup.ToLowerInvariant(), a.Day))))
        {
            await alerts.SendAsync(
                $"Spend spike in {spike.ResourceGroup}",
                $"${spike.Cost:F2} on {spike.Day} against a usual ${spike.Baseline:F2}/day",
                priority: 4, clickPath: "/azure", tags: ["money_with_wings"], ct: CancellationToken.None);
        }
    }
}

