namespace PoPunkouterSoftware.Shared;

/// <summary>
/// A GitHub-contributions-style grid of the last N days × every tracked service, built from
/// the per-service snapshots already persisted on each <see cref="HistorySummary"/>
/// (<see cref="ServiceHistoryPoint"/>). No new storage: the data has been written on every
/// scan since history existed, it simply had nowhere to be shown.
///
/// <para>Scans are irregular — several in one day, none the next — so a day's cell is the
/// <em>worst</em> state observed that day. One bad scan in a day marks the day bad, which is
/// the honest reading for an uptime grid.</para>
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public record UptimeHeatmap
{
    /// <summary>Oldest-first, one entry per calendar day in the window, including days with no scan.</summary>
    public List<DateTime> Days { get; init; } = new();

    /// <summary>One row per service, ordered worst-uptime-first so problems sort to the top.</summary>
    public List<UptimeRow> Rows { get; init; } = new();

    /// <summary>How many full Azure scans the grid was built from — the UI says so rather than implying daily coverage.</summary>
    public int ScanCount { get; init; }

    /// <summary>
    /// How many background pinger probes contributed. Usually far larger than
    /// <see cref="ScanCount"/>, since the pinger runs every few minutes while a full scan runs
    /// only on demand — reporting scans alone would understate what the grid rests on.
    /// </summary>
    public int PingCount { get; init; }
}

/// <summary>One service's row in the grid.</summary>
public record UptimeRow
{
    public string Name { get; init; } = "";

    /// <summary>
    /// Parallel to <see cref="UptimeHeatmap.Days"/> — same length, same order. A
    /// <see cref="UptimeState.NoData"/> cell means no scan covered that day.
    /// </summary>
    public List<string> Cells { get; init; } = new();

    /// <summary>
    /// Healthy observations as a percentage of all observations of this service in the window
    /// — counted per SCAN, not per day. 100 when the row has no data at all.
    ///
    /// <para>Per-day would be badly misleading at this app's scan cadence. Cells are
    /// worst-wins, which is right for the grid (a day containing an outage is not a green
    /// day), but carrying that into the percentage makes one bad scan out of three in a single
    /// day read as "0% uptime" — directly contradicting a status paragraph that correctly
    /// calls the same app healthy. Per-scan says 67%, which is what actually happened.</para>
    /// </summary>
    public int UptimePercent { get; init; }

    /// <summary>Days in the window with at least one scan — context for the grid's density.</summary>
    public int DaysWithData { get; init; }

    /// <summary>Scans that observed this service — the denominator behind <see cref="UptimePercent"/>.</summary>
    public int ScansObserved { get; init; }

    /// <summary>
    /// Indexes into <see cref="UptimeHeatmap.Days"/> on which a deploy landed. A floor, not a
    /// complete log: each scan records only the latest deploy, so two on one day between scans
    /// show as one marker.
    /// </summary>
    public List<int> DeployDays { get; init; } = new();

    /// <summary>
    /// OBSERVED days since this row's most recent down or degraded day — the streak on the
    /// home-page card and the README badge. Unobserved days count for nothing, so it reaches the
    /// window length (the UI says "30+") only when every day was seen and clean; null when the
    /// service was never observed, because an unobserved service has no streak to claim.
    /// </summary>
    public int? CleanDays { get; init; }

    /// <summary>Exceptions in the 24 hours before the latest scan, from Application Insights. Null when none were logged.</summary>
    public int? Errors24h { get; init; }

    /// <summary>The most frequent exception TYPE behind <see cref="Errors24h"/>.</summary>
    public string? TopError { get; init; }

    /// <summary>Open Dependabot alerts on the app's repo at the latest scan. Null when unknown.</summary>
    public int? SecurityAlerts { get; init; }

    /// <summary>
    /// The latest scan's probe verdict as an <see cref="UptimeState"/> — the same fact the home
    /// card's badge shows. Today's cell is worst-wins, so one failed ping this morning would
    /// call an app that answers 200 right now "Down". Null when the latest scan did not see it.
    /// </summary>
    public string? Current { get; init; }

    /// <summary>Up to ten exception types behind <see cref="Errors24h"/>, for the app drawer.</summary>
    public List<AppErrorType> ErrorTypes { get; init; } = new();

    /// <summary>The repo's latest push-triggered workflow run — a failed one is a red build. Null when unknown.</summary>
    public DeployInfo? LastRun { get; init; }

    /// <summary>The catalog's content expectation, checked at the latest scan.</summary>
    public ContentCheckInfo? ContentCheck { get; init; }

    /// <summary>The repo's package freshness at the latest scan.</summary>
    public PackageHealthInfo? Packages { get; init; }

    /// <summary>The app's App Service plan SKU ("F1"); null for anything that is not an App Service site.</summary>
    public string? PlanSku { get; init; }

    /// <summary>What the paid tiers would cost per month in this app's region and OS, cheapest first.</summary>
    public List<TierPrice> TierOptions { get; init; } = new();
}

/// <summary>
/// One service's observed reachability on one day, rolled up from the background pinger's
/// sweeps rather than from a full Azure scan.
///
/// <para>This exists because full scans are not scheduled — they happen on a manual rescan or
/// when a visitor loads a stale portfolio, so on a day with no traffic the grid would have no
/// data at all. The pinger already probes every service every few minutes; this persists that
/// as a compact per-day tally (one row per service per day) instead of discarding it into
/// metrics. Scan observations and ping observations are merged when the grid is built.</para>
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public record UptimeDaySample
{
    /// <summary>UTC date this tally covers, at midnight.</summary>
    public DateTime Day { get; init; }

    public string ServiceName { get; init; } = "";

    /// <summary>Probes that found the service reachable.</summary>
    public int HealthyCount { get; init; }

    /// <summary>Probes attempted. Always ≥ <see cref="HealthyCount"/>.</summary>
    public int TotalCount { get; init; }
}

/// <summary>Canonical <see cref="UptimeRow.Cells"/> values.</summary>
public static class UptimeState
{
    public const string Healthy = "healthy";
    public const string Broken = "broken";

    /// <summary>Scanned that day, but the status was neither clearly healthy nor clearly broken.</summary>
    public const string Degraded = "degraded";

    /// <summary>No scan covered that day.</summary>
    public const string NoData = "no-data";

    /// <summary>Worst-wins ordering when a day holds several scans. Pure — no I/O.</summary>
    public static int Rank(string state) => state switch
    {
        Broken => 0,
        Degraded => 1,
        Healthy => 2,
        _ => 3,
    };

    /// <summary>The state to record for a day given two observations of it.</summary>
    public static string Worst(string a, string b) => Rank(a) <= Rank(b) ? a : b;
}
