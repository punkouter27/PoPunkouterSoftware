using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

// DashboardInsightsBuilder is pure and I/O-free, so it belongs to this tier. The three
// insights it derives (what-changed, cost forecast, uptime grid) are read straight onto the
// dashboard, so a silent mistake here is a dashboard that lies confidently.

public class ScanDeltaTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>No exclusions — the predicate the API supplies is tested separately.</summary>
    private static bool NothingExcluded(string?[] _) => false;

    private static AzureReport ReportWith(
        DateTime at, params (string Name, string Status, int ResponseMs)[] services) => new()
    {
        GeneratedAt = at,
        WebServices = new WebServicesInfo
        {
            Services = services.Select(s => new WebService
            {
                Name = s.Name,
                FriendlyName = s.Name,
                HttpStatus = s.Status,
                Connectivity = new ConnectivityInfo { Success = s.Status == ServiceHealth.Active, ResponseTime = s.ResponseMs },
            }).ToList(),
        },
    };

    private static HistorySummary HistoryWith(
        DateTime at, params (string Name, string Status)[] services) => new()
    {
        GeneratedAt = at,
        Services = services.Select(s => new ServiceHistoryPoint { Name = s.Name, HttpStatus = s.Status }).ToList(),
    };

    [Fact]
    public void NoPriorScan_ReportsBaselineRatherThanNoChanges()
    {
        var delta = DashboardInsightsBuilder.BuildDelta(
            ReportWith(Now, ("app-a", ServiceHealth.Active, 100)), [], NothingExcluded);

        delta.HasBaseline.Should().BeFalse(
            because: "an empty change list on a first scan would read as 'nothing changed', which is a different claim");
        delta.Changes.Should().BeEmpty();
    }

    [Fact]
    public void CurrentScansOwnHistoryRow_IsNotDiffedAgainstItself()
    {
        // The scan's own HistorySummary row is written at save time, so by the time the read
        // path runs it is already in the window. Diffing against it would report "nothing
        // changed" on every single page load.
        var current = ReportWith(Now, ("app-a", ServiceHealth.Broken, 0));
        var history = new List<HistorySummary>
        {
            HistoryWith(Now, ("app-a", ServiceHealth.Broken)),                 // this scan
            HistoryWith(Now.AddHours(-6), ("app-a", ServiceHealth.Active)),    // the real baseline
        };

        var delta = DashboardInsightsBuilder.BuildDelta(current, history, NothingExcluded);

        delta.PreviousScanAt.Should().Be(Now.AddHours(-6));
        delta.Changes.Should().ContainSingle()
            .Which.Kind.Should().Be(ScanChangeKinds.ServiceDown);
    }

    [Fact]
    public void ServiceGoingDown_IsWorse_AndRecoveryIsBetter()
    {
        var history = new List<HistorySummary>
        {
            HistoryWith(Now.AddHours(-1), ("app-a", ServiceHealth.Active), ("app-b", ServiceHealth.Broken)),
        };
        var current = ReportWith(Now, ("app-a", ServiceHealth.Broken, 0), ("app-b", ServiceHealth.Active, 200));
        // app-a was deployed between the two scans; app-b's deploy predates the baseline.
        current = current with
        {
            WebServices = current.WebServices! with
            {
                Services =
                [
                    current.WebServices!.Services[0] with
                    {
                        LastDeploy = new DeployInfo { CompletedAt = Now.AddMinutes(-20), Conclusion = "success", Sha = "a1b2c3d4e5f6" },
                    },
                    current.WebServices!.Services[1] with
                    {
                        LastDeploy = new DeployInfo { CompletedAt = Now.AddHours(-3), Conclusion = "success" },
                    },
                ],
            },
        };

        var delta = DashboardInsightsBuilder.BuildDelta(current, history, NothingExcluded);

        delta.Changes.Should().Contain(c =>
            c.Kind == ScanChangeKinds.ServiceDown && c.Direction == ScanChangeDirection.Worse && c.Text.Contains("app-a"));
        delta.Changes.Should().Contain(c =>
            c.Kind == ScanChangeKinds.ServiceRecovered && c.Direction == ScanChangeDirection.Better && c.Text.Contains("app-b"));

        // "Did I break it?": the outage names the deploy that landed since the last scan, and
        // the deploy is its own line. app-b deployed before the baseline, so it gets neither.
        delta.Changes.Single(c => c.Kind == ScanChangeKinds.ServiceDown).Detail
            .Should().Contain("deployed a1b2c3d (success) since the last scan");
        delta.Changes.Where(c => c.Kind == ScanChangeKinds.Deploy).Should().ContainSingle()
            .Which.Text.Should().Be("app-a was deployed");
    }

    [Fact]
    public void MeaningfulCostRise_IsReportedAsWorse()
    {
        var history = new List<HistorySummary> { new() { GeneratedAt = Now.AddHours(-1), TotalCost30Days = 12.00 } };
        var current = ReportWith(Now) with { Cost = new CostInfo { TotalCost30Days = 18.50 } };

        var change = DashboardInsightsBuilder.BuildDelta(current, history, NothingExcluded)
            .Changes.Should().ContainSingle(c => c.Kind == ScanChangeKinds.Cost).Subject;

        change.Direction.Should().Be(ScanChangeDirection.Worse);
        change.Text.Should().Contain("$6.50");
    }

    [Fact]
    public void ExcludedServices_NeverAppearInTheChangeList()
    {
        var history = new List<HistorySummary>
        {
            HistoryWith(Now.AddHours(-1), ("PoPunkouterSoftware", ServiceHealth.Active)),
        };
        var current = ReportWith(Now, ("PoPunkouterSoftware", ServiceHealth.Broken, 0));

        DashboardInsightsBuilder.BuildDelta(current, history, names =>
                names.Any(n => n == "PoPunkouterSoftware"))
            .Changes.Should().BeEmpty(
                because: "the dashboard must never report on the site rendering it");
    }
}

public class CostForecastTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoBudgetConfigured_StillCarriesTheProjectionButNoVerdict()
    {
        var report = new AzureReport
        {
            GeneratedAt = Now,
            Cost = new CostInfo { TotalCost30Days = 22.01 },
            BurnRate = new BurnRateInfo
            {
                ProjectedMonthTotal = 7.08,
                DailyCosts = [new() { Date = "2026-08-01", Cost = 3.00 }, new() { Date = "2026-08-02", Cost = 1.34 }],
            },
        };

        var forecast = DashboardInsightsBuilder.BuildForecast(report, [], budgetUsd: null);

        forecast.ProjectedMonthTotal.Should().Be(7.08);
        // "Spent so far" is month to date, not the 30-day total — the live card once showed
        // $22.01 spent beside a $7.08 projection for the same month.
        forecast.SpentToDate.Should().Be(4.34);
        forecast.PercentOfBudget.Should().BeNull();
        forecast.Verdict.Should().Be(CostVerdict.Unknown);
    }

    [Theory]
    [InlineData(20, 40, CostVerdict.Under)]
    [InlineData(32, 40, CostVerdict.Near)]   // exactly 80%
    [InlineData(41, 40, CostVerdict.Over)]
    public void VerdictBands(double projected, double budget, string expected) =>
        CostVerdict.For(projected, budget).Should().Be(expected);

    [Fact]
    public void ProjectionDelta_ComparesAgainstThePreviousScan()
    {
        var report = new AzureReport
        {
            GeneratedAt = Now,
            BurnRate = new BurnRateInfo { ProjectedMonthTotal = 38.00 },
        };
        var history = new List<HistorySummary>
        {
            new() { GeneratedAt = Now.AddDays(-1), ProjectedMonthCost = 30.00 },
        };

        DashboardInsightsBuilder.BuildForecast(report, history, budgetUsd: 40)
            .ProjectionDelta.Should().Be(8.00);
    }
}

public class CostAnomalyTests
{
    private static ResourceGroupCost Group(string name, double latest, double usual, int days = 14) => new()
    {
        Name = name,
        Daily = Enumerable.Range(1, days)
            .Select(i => new DailyCostEntry { Date = new DateTime(2026, 9, 25).AddDays(-i).ToString("yyyy-MM-dd"), Cost = usual })
            .Append(new DailyCostEntry { Date = "2026-09-25", Cost = latest })
            .Where(d => d.Cost > 0)   // Cost Management omits $0 days
            .ToList(),
    };

    /// <summary>All four branches in one table, because they are one rule read four ways.</summary>
    [Fact]
    public void OnlyARealDollarSpikeAgainstItsOwnBaselineIsFlagged()
    {
        var anomalies = DashboardInsightsBuilder.BuildCostAnomalies(
        [
            Group("rg-steady", latest: 2.10, usual: 2.00),       // normal wobble
            Group("rg-spike", latest: 9.00, usual: 1.00),        // the case this exists for
            Group("rg-pennies", latest: 0.40, usual: 0.05),      // 8x, but forty cents
            Group("rg-new", latest: 3.00, usual: 0, days: 0),    // new spend, no history
        ]);

        anomalies.Select(a => a.ResourceGroup).Should().Equal("rg-spike", "rg-new");
        anomalies[0].Should().Be(new CostAnomaly("rg-spike", "2026-09-25", 9.00, 1.00));
        DashboardInsightsBuilder.BuildCostAnomalies([]).Should().BeEmpty();
    }
}

public class UptimeHeatmapTests
{
    private static readonly DateTime Today = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static bool NothingExcluded(string?[] _) => false;

    private static HistorySummary Scan(DateTime at, params (string Name, string Status)[] services) => new()
    {
        GeneratedAt = at,
        Services = services.Select(s => new ServiceHistoryPoint { Name = s.Name, HttpStatus = s.Status }).ToList(),
    };

    [Fact]
    public void WindowIsOneCellPerDayIncludingDaysWithNoScan_WithDeployDaysMarked()
    {
        // Today's scan saw a deploy two days ago: the marker lands on THAT day's column even
        // though no scan ran on it, and a deploy older than the window is dropped.
        var scan = Scan(Today, ("app-a", ServiceHealth.Active));
        scan = scan with { Services = [scan.Services[0] with { LastDeployAt = Today.AddDays(-2) }] };
        var old = Scan(Today.AddDays(-20), ("app-a", ServiceHealth.Active));
        old = old with { Services = [old.Services[0] with { LastDeployAt = Today.AddDays(-20) }] };

        // The latest scan's per-app facts ride on the row: App Insights reports its own role
        // name, so "App-A" must still find "app-a".
        var latest = new AzureReport
        {
            WebServices = new WebServicesInfo { Services = [new WebService { Name = "app-a", OpenSecurityAlerts = 3 }] },
            AppErrors = [new AppErrorSummary { App = "App-A", Count24h = 12, TopType = "System.TimeoutException" }],
        };

        var map = DashboardInsightsBuilder.BuildUptime([scan, old], Today, NothingExcluded, windowDays: 7, latest: latest);

        map.Days.Should().HaveCount(7);
        map.Rows.Should().ContainSingle().Which.Cells.Should().HaveCount(7);
        map.Rows[0].Cells.Count(c => c == UptimeState.NoData).Should().Be(6);
        map.Rows[0].DeployDays.Should().Equal(4);
        map.Rows[0].CleanDays.Should().Be(1, "only the one observed day counts — six blank days are not a clean streak");
        map.Rows[0].Should().Match<UptimeRow>(r =>
            r.Errors24h == 12 && r.TopError == "System.TimeoutException" && r.SecurityAlerts == 3);
    }

    [Fact]
    public void SeveralScansInOneDay_TakeTheWorstState()
    {
        // One bad scan in a day marks the day bad. Taking the last scan instead would let a
        // recovery at 23:00 erase an outage that lasted all afternoon.
        var history = new List<HistorySummary>
        {
            Scan(Today.Date.AddHours(9), ("app-a", ServiceHealth.Active)),
            Scan(Today.Date.AddHours(14), ("app-a", ServiceHealth.Broken)),
            Scan(Today.Date.AddHours(23), ("app-a", ServiceHealth.Active)),
        };

        var map = DashboardInsightsBuilder.BuildUptime(history, Today, NothingExcluded, windowDays: 3);

        map.Rows[0].Cells[^1].Should().Be(UptimeState.Broken);
        map.Rows[0].CleanDays.Should().Be(0, "a bad day today ends the streak");
    }

    [Fact]
    public void DaysWithNoScan_AreExcludedFromThePercentageRatherThanCountedHealthy()
    {
        var history = new List<HistorySummary>
        {
            Scan(Today.Date, ("app-a", ServiceHealth.Active)),
            Scan(Today.Date.AddDays(-1), ("app-a", ServiceHealth.Broken)),
        };

        var row = DashboardInsightsBuilder.BuildUptime(history, Today, NothingExcluded, windowDays: 30).Rows[0];

        row.DaysWithData.Should().Be(2);
        row.ScansObserved.Should().Be(2);
        row.UptimePercent.Should().Be(50,
            because: "28 unscanned days must not be counted as uptime");
    }

    [Fact]
    public void PercentageCountsScansNotDays_SoOneBadScanIsNotAWholeDayOfDowntime()
    {
        // The cell is worst-wins (correct for the grid), but deriving the percentage from the
        // cells would render 2-of-3 healthy scans in a single day as "0% uptime" — directly
        // contradicting a status paragraph that correctly calls the same app healthy.
        var history = new List<HistorySummary>
        {
            Scan(Today.Date.AddHours(1), ("app-a", ServiceHealth.Broken)),
            Scan(Today.Date.AddHours(2), ("app-a", ServiceHealth.Active)),
            Scan(Today.Date.AddHours(3), ("app-a", ServiceHealth.Active)),
        };

        var row = DashboardInsightsBuilder.BuildUptime(history, Today, NothingExcluded, windowDays: 30).Rows[0];

        row.Cells[^1].Should().Be(UptimeState.Broken, because: "the day contained a real outage");
        row.ScansObserved.Should().Be(3);
        row.UptimePercent.Should().Be(67, because: "2 of the 3 observations were healthy");
    }

    [Fact]
    public void RowsSortWorstUptimeFirst()
    {
        var history = new List<HistorySummary>
        {
            Scan(Today.Date, ("healthy-app", ServiceHealth.Active), ("broken-app", ServiceHealth.Broken)),
        };

        var map = DashboardInsightsBuilder.BuildUptime(history, Today, NothingExcluded, windowDays: 3);

        map.Rows[0].Name.Should().Be("broken-app",
            because: "the grid exists to surface problems, not to be alphabetical");
    }

    [Fact]
    public void ExcludedServices_AreNotGraphed()
    {
        var history = new List<HistorySummary>
        {
            Scan(Today.Date, ("PoPunkouterSoftware", ServiceHealth.Active), ("app-a", ServiceHealth.Active)),
        };

        var map = DashboardInsightsBuilder.BuildUptime(
            history, Today, names => names.Any(n => n == "PoPunkouterSoftware"), windowDays: 3);

        map.Rows.Should().ContainSingle().Which.Name.Should().Be("app-a");
    }

    // ── Merging the background pinger's tallies with scan observations ─────────
    // The pinger is the only source that produces data on a day nobody visited the site, so
    // these are the cases that decide whether the grid is a real 30-day view or a record of
    // when someone happened to look.

    private static UptimeDaySample Ping(DateTime day, string name, int healthy, int total) =>
        new() { Day = day.Date, ServiceName = name, HealthyCount = healthy, TotalCount = total };

    [Fact]
    public void PingSamples_FillDaysNoScanCovered()
    {
        var scanDay = Today.Date;
        var pingOnlyDay = Today.Date.AddDays(-1);
        var history = new List<HistorySummary> { Scan(scanDay, ("app-a", ServiceHealth.Active)) };
        var pings = new List<UptimeDaySample> { Ping(pingOnlyDay, "app-a", healthy: 144, total: 144) };

        var row = DashboardInsightsBuilder.BuildUptime(
            history, Today, NothingExcluded, windowDays: 3, pingSamples: pings).Rows[0];

        row.Cells[^1].Should().Be(UptimeState.Healthy, because: "the scan covered today");
        row.Cells[^2].Should().Be(UptimeState.Healthy, because: "the pinger covered yesterday");
        row.DaysWithData.Should().Be(2);
    }

    [Fact]
    public void AScanFindingItDown_IsNotErasedByLaterHealthyPings()
    {
        var history = new List<HistorySummary> { Scan(Today.Date, ("app-a", ServiceHealth.Broken)) };
        var pings = new List<UptimeDaySample> { Ping(Today.Date, "app-a", healthy: 100, total: 100) };

        var row = DashboardInsightsBuilder.BuildUptime(
            history, Today, NothingExcluded, windowDays: 3, pingSamples: pings).Rows[0];

        row.Cells[^1].Should().Be(UptimeState.Broken, because: "cells stay worst-wins across both sources");
        row.ScansObserved.Should().Be(101, because: "both sources count toward the denominator");
        row.UptimePercent.Should().Be(99, because: "100 of the 101 observations were healthy");
    }
}

public class ServiceIdentityTests
{
    // The uptime grid joins scan rows and ping rows on this string. When the two writers
    // disagreed, every service rendered as two half-populated rows — 16 for 8 apps.

    [Fact]
    public void PrefersFriendlyName_FallingBackToResourceName()
    {
        NameMatching.ServiceIdentity("PoMemeVideo", "app-pomemevideo2").Should().Be("PoMemeVideo");
        // null and "" take the same absent-friendly-name branch; one case covers both.
        NameMatching.ServiceIdentity(null, "app-pomemevideo2").Should().Be("app-pomemevideo2");
    }
}

public class UptimeSampleRowKeyTests
{
    [Fact]
    public void RowKeyIsDatePrefixed_SoLexicalRangeQueriesAreDateRanges()
    {
        var jan = UptimeSampleStore.RowKeyFor(new DateTime(2026, 1, 5), "app-a");
        var feb = UptimeSampleStore.RowKeyFor(new DateTime(2026, 2, 5), "app-a");

        jan.Should().StartWith("20260105-");
        string.CompareOrdinal(jan, feb).Should().BeNegative(
            because: "LoadRecentAsync and PruneAsync both filter on RowKey ranges");
    }

    [Fact]
    public void RowKeyAvoidsCharactersTableStorageForbids()
    {
        // Table Storage rejects '/', '\', '#' and '?' in a RowKey, and a service name is not
        // guaranteed to avoid them — hence the hash.
        var key = UptimeSampleStore.RowKeyFor(new DateTime(2026, 1, 5), "weird/name#with?chars\\in-it");

        key.Should().MatchRegex("^[0-9]{8}-[0-9a-f]{16}$");
    }
}
