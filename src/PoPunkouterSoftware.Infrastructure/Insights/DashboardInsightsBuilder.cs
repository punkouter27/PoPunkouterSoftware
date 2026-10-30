using System.Globalization;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Derives the three history-backed dashboard insights — "what changed since last scan", the
/// month-end cost forecast, and the 30-day uptime grid — from data the app already persists:
/// the current <see cref="AzureReport"/> plus the precomputed <see cref="HistorySummary"/>
/// rows written on every save. No new storage, no new Azure calls, no live queries.
///
/// <para>Every method here is pure and I/O-free, so it is covered by the Unit tier rather
/// than Integration. Lives in Infrastructure (not Shared) for the same reason
/// <see cref="HistorySummaryMapper"/> does: the contract assembly stays a pure data carrier
/// and ships in the WASM bundle.</para>
///
/// <para>Callers pass the same <c>isExcluded</c> predicate <see cref="AttentionItemsBuilder"/>
/// takes. History rows are written unfiltered, so without it the site would appear in its own
/// uptime grid and its own change list.</para>
/// </summary>
public static class DashboardInsightsBuilder
{
    /// <summary>Dollar move below which a cost change is noise, not news.</summary>
    private const double CostChangeThresholdUsd = 0.50;

    /// <summary>Percentage-point move in average response time below which the change is noise.</summary>
    private const double ResponseChangeThresholdPercent = 25d;

    /// <summary>Days covered by the uptime grid. Matches the default history retention window.</summary>
    public const int UptimeWindowDays = 30;

    // ─── What changed since the last scan ────────────────────────────────────

    /// <summary>
    /// Compares the current report against the most recent history row that predates it.
    /// </summary>
    /// <param name="current">The report being rendered.</param>
    /// <param name="history">
    /// History rows in any order; this method sorts. May include a row for
    /// <paramref name="current"/> itself (it is written at save time) — that row is skipped
    /// by timestamp so a scan is never diffed against itself.
    /// </param>
    /// <param name="isExcluded">Same predicate <see cref="AttentionItemsBuilder.Build"/> takes.</param>
    public static ScanDelta BuildDelta(
        AzureReport current, IReadOnlyCollection<HistorySummary> history, Func<string?[], bool> isExcluded)
    {
        var currentAt = current.GeneratedAt;
        var previous = history
            .Where(h => h.GeneratedAt > DateTime.MinValue)
            // Strictly earlier than the current scan: the current scan's own row is already
            // in history by the time the read path runs, and diffing it against itself would
            // report "nothing changed" on every single load.
            .Where(h => currentAt is null || h.GeneratedAt < currentAt.Value)
            .OrderByDescending(h => h.GeneratedAt)
            .FirstOrDefault();

        if (previous is null)
        {
            return new ScanDelta
            {
                CurrentScanAt = currentAt,
                PreviousScanAt = null,
                HasBaseline = false,
                Changes = new List<ScanChange>(),
            };
        }

        var changes = new List<ScanChange>();
        var currentServices = (current.WebServices?.Services ?? new List<WebService>())
            .Where(s => !isExcluded([s.FriendlyName, s.Name]))
            .ToList();
        var previousByName = previous.Services
            .Where(s => !isExcluded([s.Name, s.Name]))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // A deploy counts for this scan when it completed after the previous scan ran and no
        // later than this one. That window is what lets "went offline" name its likely cause.
        var baselineAt = previous.GeneratedAt;
        bool DeployedSincePrevious(WebService s) =>
            s.LastDeploy is { } d && d.CompletedAt > baselineAt
            && (currentAt is null || d.CompletedAt <= currentAt.Value);

        // ── Health transitions: the most actionable change there is, so it leads. ──
        foreach (var svc in currentServices)
        {
            var name = string.IsNullOrWhiteSpace(svc.FriendlyName) ? svc.Name : svc.FriendlyName;
            if (!previousByName.TryGetValue(name, out var before))
                continue;

            var wasHealthy = ServiceHealth.IsHealthy(before.HttpStatus);
            var isHealthy = ServiceHealth.IsHealthy(svc.HttpStatus);
            if (wasHealthy == isHealthy)
                continue;

            var transition = $"was {Describe(before.HttpStatus)}, now {Describe(svc.HttpStatus)}";
            changes.Add(isHealthy
                ? new ScanChange(
                    ScanChangeKinds.ServiceRecovered, ScanChangeDirection.Better,
                    $"{name} came back online", transition)
                : new ScanChange(
                    ScanChangeKinds.ServiceDown, ScanChangeDirection.Worse,
                    $"{name} went offline",
                    // "Did I break it?" answered in place: a deploy between the two scans is
                    // the first suspect, so the outage line names it.
                    DeployedSincePrevious(svc)
                        ? $"{transition} · deployed {DescribeDeploy(svc.LastDeploy!)} since the last scan"
                        : transition));
        }

        // ── Deploys that landed between the two scans ──
        foreach (var svc in currentServices.Where(DeployedSincePrevious))
        {
            var name = string.IsNullOrWhiteSpace(svc.FriendlyName) ? svc.Name : svc.FriendlyName;
            var deploy = svc.LastDeploy!;
            var failed = deploy.Conclusion is { } c && !c.Equals("success", StringComparison.OrdinalIgnoreCase);
            changes.Add(new ScanChange(
                ScanChangeKinds.Deploy,
                failed ? ScanChangeDirection.Worse : ScanChangeDirection.Neutral,
                failed ? $"{name} deploy {deploy.Conclusion}" : $"{name} was deployed",
                DescribeDeploy(deploy)));
        }

        // Services appearing/disappearing between scans. A service that vanished may have been
        // deleted or merely missed by the scan, so it is reported neutrally rather than as loss.
        foreach (var svc in currentServices)
        {
            var name = string.IsNullOrWhiteSpace(svc.FriendlyName) ? svc.Name : svc.FriendlyName;
            if (!previousByName.ContainsKey(name))
                changes.Add(new ScanChange(
                    ScanChangeKinds.Resources, ScanChangeDirection.Neutral,
                    $"{name} appeared in the inventory"));
        }

        var currentNames = currentServices
            .Select(s => string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in previousByName.Keys.Where(n => !currentNames.Contains(n)))
        {
            changes.Add(new ScanChange(
                ScanChangeKinds.Resources, ScanChangeDirection.Neutral,
                $"{gone} is no longer in the inventory"));
        }

        // ── Cost ──
        var costNow = current.Cost?.TotalCost30Days ?? 0;
        var costBefore = previous.TotalCost30Days;
        var costDelta = costNow - costBefore;
        if (Math.Abs(costDelta) >= CostChangeThresholdUsd)
        {
            changes.Add(new ScanChange(
                ScanChangeKinds.Cost,
                costDelta > 0 ? ScanChangeDirection.Worse : ScanChangeDirection.Better,
                costDelta > 0
                    ? $"30-day cost rose by ${Math.Abs(costDelta):F2}"
                    : $"30-day cost fell by ${Math.Abs(costDelta):F2}",
                $"${costBefore:F2} → ${costNow:F2}"));
        }

        // ── Resource count ──
        var resourcesNow = current.AllResourceSummary?.Total ?? 0;
        var resourceDelta = resourcesNow - previous.TotalResources;
        if (resourceDelta != 0 && previous.TotalResources > 0)
        {
            changes.Add(new ScanChange(
                ScanChangeKinds.Resources, ScanChangeDirection.Neutral,
                resourceDelta > 0
                    ? $"{resourceDelta} resource{(resourceDelta == 1 ? "" : "s")} added"
                    : $"{Math.Abs(resourceDelta)} resource{(resourceDelta == -1 ? "" : "s")} removed",
                $"{previous.TotalResources} → {resourcesNow}"));
        }

        // ── Average response time ──
        var responseNow = currentServices
            .Where(s => s.Connectivity?.Success == true)
            .Select(s => (double)(s.Connectivity?.ResponseTime ?? 0))
            .DefaultIfEmpty(0).Average();
        if (previous.AvgResponseTimeMs > 0 && responseNow > 0)
        {
            var pct = (responseNow - previous.AvgResponseTimeMs) / previous.AvgResponseTimeMs * 100d;
            if (Math.Abs(pct) >= ResponseChangeThresholdPercent)
            {
                changes.Add(new ScanChange(
                    ScanChangeKinds.Performance,
                    pct > 0 ? ScanChangeDirection.Worse : ScanChangeDirection.Better,
                    pct > 0
                        ? $"Average response time is {Math.Abs(pct):F0}% slower"
                        : $"Average response time is {Math.Abs(pct):F0}% faster",
                    $"{previous.AvgResponseTimeMs:F0} ms → {responseNow:F0} ms"));
            }
        }

        // Worse news first, then better, then neutral bookkeeping.
        var ordered = changes
            .OrderBy(c => c.Direction switch
            {
                ScanChangeDirection.Worse => 0,
                ScanChangeDirection.Better => 1,
                _ => 2,
            })
            .ThenBy(c => c.Kind switch
            {
                ScanChangeKinds.ServiceDown => 0,
                ScanChangeKinds.ServiceRecovered => 1,
                ScanChangeKinds.Deploy => 2,
                ScanChangeKinds.Security => 3,
                ScanChangeKinds.Cost => 4,
                ScanChangeKinds.Performance => 5,
                _ => 6,
            })
            .ToList();

        return new ScanDelta
        {
            CurrentScanAt = currentAt,
            PreviousScanAt = previous.GeneratedAt,
            HasBaseline = true,
            Changes = ordered,
        };
    }

    private static string Describe(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "unknown" : status;

    /// <summary>"a1b2c3d (success)" — short SHA when there is one.</summary>
    private static string DescribeDeploy(DeployInfo deploy)
    {
        var sha = deploy.Sha is { Length: >= 7 } s ? s[..7] : "a build";
        return deploy.Conclusion is { Length: > 0 } c ? $"{sha} ({c})" : sha;
    }

    // ─── Cost forecast ───────────────────────────────────────────────────────

    /// <summary>
    /// Relates the scan's own burn-rate projection to the configured budget. The projection is
    /// not recomputed here — <c>AzureReport.BurnRate.ProjectedMonthTotal</c> is produced during
    /// the scan and this only frames it.
    /// </summary>
    /// <param name="budgetUsd">
    /// <c>Budget:MonthlyUsd</c>, or null/non-positive when unset — in which case the forecast
    /// carries the projection with <see cref="CostVerdict.Unknown"/> and no ring.
    /// </param>
    public static CostForecast BuildForecast(
        AzureReport current, IReadOnlyCollection<HistorySummary> history, double? budgetUsd)
    {
        // Month to date, from the same series the projection is extrapolated from. This was the
        // rolling 30-day total, so on 2026-10-19 the card read "spent $22.01, projected $7.08"
        // — $17.67 of those 30 days had landed in September. Both numbers were true; side by
        // side under "so far" and "month" they contradicted each other.
        var spent = current.BurnRate?.DailyCosts is { Count: > 0 } days
            ? days.Sum(d => d.Cost)
            : current.Cost?.TotalCost30Days ?? 0;
        var projected = current.BurnRate?.ProjectedMonthTotal ?? 0;

        // A scan that produced no burn-rate step still has a usable answer: spend to date is
        // a floor on the month total, and showing it beats showing $0.00 "projected".
        if (projected <= 0)
            projected = spent;

        var currentAt = current.GeneratedAt;
        var previous = history
            .Where(h => h.GeneratedAt > DateTime.MinValue && h.ProjectedMonthCost > 0)
            .Where(h => currentAt is null || h.GeneratedAt < currentAt.Value)
            .OrderByDescending(h => h.GeneratedAt)
            .FirstOrDefault();

        var effectiveBudget = budgetUsd is > 0 ? budgetUsd : null;
        return new CostForecast
        {
            SpentToDate = Math.Round(spent, 2),
            ProjectedMonthTotal = Math.Round(projected, 2),
            BudgetUsd = effectiveBudget,
            PercentOfBudget = effectiveBudget is null
                ? null
                : (int)Math.Round(Math.Max(0, projected / effectiveBudget.Value * 100d)),
            Verdict = CostVerdict.For(projected, effectiveBudget),
            ProjectionDelta = previous is null
                ? null
                : Math.Round(projected - previous.ProjectedMonthCost, 2),
            Anomalies = BuildCostAnomalies(current.Cost?.ResourceGroups),
        };
    }

    /// <summary>Days of history each group's latest day is compared against.</summary>
    public const int CostBaselineDays = 14;

    /// <summary>Dollar rise over baseline below which a spike is noise, however large in ratio.</summary>
    private const double CostSpikeFloorUsd = 1.00;

    /// <summary>
    /// Resource groups whose most recent day of spend is out of character: at least
    /// <see cref="CostSpikeFloorUsd"/> above their own <see cref="CostBaselineDays"/>-day mean,
    /// at least double it, AND more than three standard deviations above it. All three, so a
    /// cent-level group doubling is not news and a naturally lumpy group is judged against its
    /// own lumpiness. A group with no baseline spend that suddenly costs a dollar a day is
    /// flagged — new spend is exactly what this exists to catch.
    ///
    /// <para>"Most recent day" is the latest date in the whole series, not today: Cost
    /// Management lags by up to a day, and a partial latest day can only under-report, so it
    /// cannot raise a false alarm.</para>
    /// </summary>
    public static List<CostAnomaly> BuildCostAnomalies(IReadOnlyCollection<ResourceGroupCost>? groups)
    {
        var latest = (groups ?? []).SelectMany(g => g.Daily).Select(d => d.Date)
            .DefaultIfEmpty("").Max(StringComparer.Ordinal);
        if (!DateTime.TryParseExact(latest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var latestDay))
            return [];

        var baselineDays = Enumerable.Range(1, CostBaselineDays)
            .Select(i => latestDay.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        var anomalies = new List<CostAnomaly>();
        foreach (var group in groups!)
        {
            var byDay = group.Daily.GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Sum(d => d.Cost));
            var cost = byDay.GetValueOrDefault(latest!);
            // Missing days are $0 days: Cost Management omits rows with no spend.
            var baseline = baselineDays.Select(d => byDay.GetValueOrDefault(d)).ToList();
            var mean = baseline.Average();
            var sd = Math.Sqrt(baseline.Average(v => (v - mean) * (v - mean)));

            if (cost - mean >= CostSpikeFloorUsd && cost >= 2 * mean && cost > mean + 3 * sd)
                anomalies.Add(new CostAnomaly(group.Name, latest!, Math.Round(cost, 2), Math.Round(mean, 2)));
        }

        return anomalies.OrderByDescending(a => a.Cost - a.Baseline).ToList();
    }

    // ─── Uptime heatmap ──────────────────────────────────────────────────────

    /// <summary>
    /// Collapses every observation in the window onto a day × service grid, from BOTH sources:
    /// per-service snapshots inside each <see cref="HistorySummary"/> (written only when a full
    /// Azure scan runs) and the background pinger's per-day tallies
    /// (<see cref="UptimeDaySample"/>, written every few minutes while the app is awake).
    ///
    /// <para>Two sources are needed because neither covers the calendar alone: scans happen
    /// only on a manual rescan or a stale-portfolio page load, so an untrafficked day has none;
    /// and the pinger stops with the app, which an F1 plan unloads when idle. Merging them
    /// means a day counts as observed if either saw it.</para>
    /// </summary>
    /// <param name="today">
    /// The last day in the window. Injected rather than read from the clock so the result is
    /// deterministic and unit-testable (the codebase registers <c>TimeProvider</c> for the
    /// same reason).
    /// </param>
    /// <param name="pingSamples">
    /// Pinger tallies for the window. Empty is normal (pinger disabled, storage down, nothing
    /// recorded yet) and simply falls back to scan observations alone.
    /// </param>
    public static UptimeHeatmap BuildUptime(
        IReadOnlyCollection<HistorySummary> history,
        DateTime today,
        Func<string?[], bool> isExcluded,
        int windowDays = UptimeWindowDays,
        IReadOnlyCollection<UptimeDaySample>? pingSamples = null,
        AzureReport? latest = null)
    {
        var lastDay = today.Date;
        var firstDay = lastDay.AddDays(-(windowDays - 1));
        var days = Enumerable.Range(0, windowDays).Select(i => firstDay.AddDays(i)).ToList();
        var dayIndex = days
            .Select((d, i) => (d, i))
            .ToDictionary(t => t.d, t => t.i);

        var inWindow = history
            .Where(h => h.GeneratedAt > DateTime.MinValue)
            .Where(h => h.GeneratedAt.Date >= firstDay && h.GeneratedAt.Date <= lastDay)
            .ToList();

        // service name -> day index -> worst state seen that day
        var grid = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // service name -> (healthy observations, total observations), counted per SCAN.
        // The percentage is deliberately not derived from the day cells: those are worst-wins,
        // so one bad scan among three in a single day would render as "0% uptime" beside a
        // status paragraph correctly calling the same app healthy.
        var observations = new Dictionary<string, (int Healthy, int Total)>(StringComparer.OrdinalIgnoreCase);

        foreach (var scan in inWindow)
        {
            if (!dayIndex.TryGetValue(scan.GeneratedAt.Date, out var col))
                continue;

            foreach (var svc in scan.Services)
            {
                if (string.IsNullOrWhiteSpace(svc.Name) || isExcluded([svc.Name, svc.Name]))
                    continue;

                if (!grid.TryGetValue(svc.Name, out var row))
                {
                    row = new string[windowDays];
                    Array.Fill(row, UptimeState.NoData);
                    grid[svc.Name] = row;
                }

                var state = ServiceHealth.IsHealthy(svc.HttpStatus) ? UptimeState.Healthy
                    : ServiceHealth.IsBroken(svc.HttpStatus) ? UptimeState.Broken
                    : UptimeState.Degraded;

                row[col] = row[col] == UptimeState.NoData ? state : UptimeState.Worst(row[col], state);

                var seen = observations.GetValueOrDefault(svc.Name);
                observations[svc.Name] = (
                    seen.Healthy + (state == UptimeState.Healthy ? 1 : 0),
                    seen.Total + 1);
            }
        }

        // ── Fold in the pinger's per-day tallies ──
        // A day the pinger observed is a real observation of that day even when no scan ran,
        // so it fills a NoData cell and adds to the denominator. Where both sources saw a day,
        // the cell stays worst-wins: a scan finding the service down is not erased by the
        // pinger finding it up an hour later.
        var pingCount = 0;
        foreach (var sample in pingSamples ?? [])
        {
            if (string.IsNullOrWhiteSpace(sample.ServiceName)
                || sample.TotalCount <= 0
                || isExcluded([sample.ServiceName, sample.ServiceName])
                || !dayIndex.TryGetValue(sample.Day.Date, out var col))
                continue;

            pingCount += sample.TotalCount;

            if (!grid.TryGetValue(sample.ServiceName, out var row))
            {
                row = new string[windowDays];
                Array.Fill(row, UptimeState.NoData);
                grid[sample.ServiceName] = row;
            }

            // Any failed probe that day makes the day's cell Broken; an all-clear day is Healthy.
            var dayState = sample.HealthyCount == sample.TotalCount ? UptimeState.Healthy : UptimeState.Broken;
            row[col] = row[col] == UptimeState.NoData ? dayState : UptimeState.Worst(row[col], dayState);

            var seenSoFar = observations.GetValueOrDefault(sample.ServiceName);
            observations[sample.ServiceName] = (
                seenSoFar.Healthy + sample.HealthyCount,
                seenSoFar.Total + sample.TotalCount);
        }

        // ── Deploy markers ──
        // Every scan records the service's latest deploy, so the distinct dates across ALL
        // history rows (not just those in the window — a scan after the window can still name
        // a deploy inside it) are the deploy days. Only for services the grid already draws.
        var deployDays = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in history.SelectMany(h => h.Services))
        {
            if (point.LastDeployAt is not DateTime at
                || !grid.ContainsKey(point.Name)
                || !dayIndex.TryGetValue(at.Date, out var col))
                continue;

            if (!deployDays.TryGetValue(point.Name, out var set))
                deployDays[point.Name] = set = new SortedSet<int>();
            set.Add(col);
        }

        var rows = grid
            .Select(kvp =>
            {
                var seen = observations.GetValueOrDefault(kvp.Key);
                var lastBad = Array.FindLastIndex(kvp.Value, c => c is UptimeState.Broken or UptimeState.Degraded);
                return new UptimeRow
                {
                    Name = kvp.Key,
                    Cells = kvp.Value.ToList(),
                    DaysWithData = kvp.Value.Count(c => c != UptimeState.NoData),
                    ScansObserved = seen.Total,
                    UptimePercent = seen.Total == 0 ? 100 : (int)Math.Round(seen.Healthy * 100d / seen.Total),
                    DeployDays = deployDays.TryGetValue(kvp.Key, out var d) ? d.ToList() : new List<int>(),
                    // Observed days only: an unobserved day is not a clean one, so an app first
                    // seen yesterday has a 1-day streak, not the whole window.
                    CleanDays = seen.Total == 0 ? null : kvp.Value.Skip(lastBad + 1).Count(c => c != UptimeState.NoData),
                };
            })
            // Worst uptime first: the grid exists to surface problems, not to be alphabetical.
            .OrderBy(r => r.UptimePercent)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UptimeHeatmap
        {
            Days = days,
            Rows = rows.Select(r => Annotate(r, latest)).ToList(),
            // Both sources counted: reporting only full scans would tell the reader the grid
            // rests on 3 observations when it actually rests on thousands of probes.
            ScanCount = inWindow.Count,
            PingCount = pingCount,
        };
    }

    /// <summary>
    /// Puts the latest scan's per-app facts on a row: exceptions from Application Insights and
    /// open Dependabot alerts. Rows are history-keyed by <see cref="NameMatching.ServiceIdentity"/>,
    /// the same key the report's services carry, so that join is exact. App Insights reports its
    /// own cloud_RoleName, which is matched on normalized name, exact first, then containment.
    /// </summary>
    private static UptimeRow Annotate(UptimeRow row, AzureReport? latest)
    {
        if (latest is null)
            return row;

        var key = NameMatching.Normalize(row.Name);
        var service = (latest.WebServices?.Services ?? [])
            .FirstOrDefault(s => NameMatching.Normalize(NameMatching.ServiceIdentity(s.FriendlyName, s.Name)) == key);
        var errors = latest.AppErrors.FirstOrDefault(e => NameMatching.Normalize(e.App) == key)
            ?? (key.Length == 0 ? null
                : latest.AppErrors.FirstOrDefault(e => NameMatching.Normalize(e.App) is { Length: > 0 } app
                    && NameMatching.FuzzyContains(app, key)));

        return row with
        {
            // Never a manufactured zero: an app that does not export to the shared component
            // logs nothing there either, and "0 errors" would claim a health it never reported.
            Errors24h = errors?.Count24h,
            TopError = errors?.TopType,
            SecurityAlerts = service?.OpenSecurityAlerts,
            ErrorTypes = errors?.Types ?? [],
            LastRun = service?.LastDeploy,
            ContentCheck = service?.ContentCheck,
            Packages = service?.Packages,
            PlanSku = service?.AppServicePlanSku,
            // Priced only for an App Service site: a Static Web App or container has no plan tier.
            TierOptions = service is { ResourceType: "Microsoft.Web/sites", Region: { } region }
                ? latest.TierPrices
                    .Where(t => t.Region == region
                        && t.Linux == (service.Kind?.Contains("linux", StringComparison.OrdinalIgnoreCase) == true))
                    .OrderBy(t => t.MonthlyUsd)
                    .ToList()
                : [],
            Current = service is null ? null
                : ServiceHealth.IsHealthy(service.HttpStatus) ? UptimeState.Healthy
                : ServiceHealth.IsBroken(service.HttpStatus) ? UptimeState.Broken
                : UptimeState.Degraded,
        };
    }
}
