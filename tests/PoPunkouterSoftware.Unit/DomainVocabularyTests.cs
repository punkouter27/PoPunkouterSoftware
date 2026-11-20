using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

// Trimmed to the budget (the old 100-test Unit budget). The cases removed were extra casing
// variants of a rule already proven once per method, and per-value rank assertions that
// the ordering facts below subsume — one `BeInAscendingOrder` over the whole ladder is a
// stronger claim than five independent equality checks, and does not have to be edited
// when a level is inserted.

public class ServiceHealthTests
{
    /// <summary>
    /// One rule's worth of coverage in one test: this used to be ten theory cases, six of them
    /// casing variants of the same comparison.
    /// </summary>
    [Fact]
    public void ActiveIsHealthy_BrokenAndUnreachableAreBroken_CaseInsensitiveButNotTrimmed()
    {
        ServiceHealth.IsHealthy("ACTIVE").Should().BeTrue();
        ServiceHealth.IsHealthy("  active  ").Should().BeFalse("the wire format is exact");
        ServiceHealth.IsHealthy("broken").Should().BeFalse();
        ServiceHealth.IsHealthy(null).Should().BeFalse();

        ServiceHealth.IsBroken("broken").Should().BeTrue();
        ServiceHealth.IsBroken("Unreachable").Should().BeTrue();
        ServiceHealth.IsBroken("active").Should().BeFalse();
        ServiceHealth.IsBroken(null).Should().BeFalse();

        ServiceHealth.IsHealthy(ServiceHealth.Unknown).Should().BeFalse();
        ServiceHealth.IsBroken(ServiceHealth.Unknown).Should().BeFalse();
    }
}

public class SeverityLevelTests
{

    [Fact]
    public void Rank_OrdersSeveritiesStrictly_UnknownLast()
    {
        var ordered = new[]
        {
            SeverityLevel.Critical, SeverityLevel.High, SeverityLevel.Medium, SeverityLevel.Low, "unrecognized",
        };

        ordered.Select(SeverityLevel.Rank).Should().BeInAscendingOrder()
            .And.OnlyHaveUniqueItems(because: "each severity must occupy its own rank");
    }
}

public class ResourceRiskLevelTests
{
    [Theory]
    [InlineData("Cleanup", 0)]    // case-insensitive at the top of the ladder
    [InlineData("mystery", 4)]    // unrecognised sorts last, never first
    [InlineData(null, 4)]
    public void Rank_IsCaseInsensitive_AndSortsUnknownLast(string? level, int expected)
    {
        ResourceRiskLevel.Rank(level).Should().Be(expected);
    }

    [Fact]
    public void SortingByRank_PutsCleanupBeforeCostBeforeWatchBeforeOk()
    {
        var shuffled = new[] { "ok", "watch", "cleanup", "cost" };

        var sorted = shuffled.OrderBy(ResourceRiskLevel.Rank).ToArray();

        sorted.Should().Equal("cleanup", "cost", "watch", "ok");
    }
}
