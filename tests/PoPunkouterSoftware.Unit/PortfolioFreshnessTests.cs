using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The staleness threshold decides whether a "Live" badge may be shown at all, so the
/// boundary behaviour is contract, not implementation detail.
/// </summary>
public class PortfolioFreshnessTests
{
    private static readonly DateTime Now = new(2026, 07, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One rule, so one test: was five Facts, one per point on the same line.
    /// </summary>
    [Fact]
    public void StaleOnlyPastTheThreshold_OrWithNoReportAtAll()
    {
        // No report can never be trusted as current data.
        PortfolioFreshness.IsStale(null, Now).Should().BeTrue();

        PortfolioFreshness.IsStale(Now.AddHours(-1), Now).Should().BeFalse();
        PortfolioFreshness.IsStale(Now - PortfolioFreshness.StaleAfter, Now).Should().BeFalse("the boundary itself is fresh");
        PortfolioFreshness.IsStale(Now - PortfolioFreshness.StaleAfter - TimeSpan.FromSeconds(1), Now).Should().BeTrue();

        // The exact scenario that shipped: a 13-day-old scan rendering green "Live" badges.
        PortfolioFreshness.IsStale(Now.AddDays(-13), Now).Should().BeTrue();
    }
}
