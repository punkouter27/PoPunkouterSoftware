using PoPunkouterSoftware.Client;

namespace PoPunkouterSoftware.Unit;

// Trimmed to the budget (CLAUDE.md: 100 Unit). Every retained case is a UNIT BOUNDARY —
// the value that flips the format, or the one just below it. The dropped cases were
// mid-bucket values (72h, 36h, 10d 23h) that can only fail if a boundary case already has.

public class RelativeTimeFormatTests
{
    [Fact]
    public void MissingOrUnboundedAge_ReturnsUnknownAge()
    {
        RelativeTime.Format((DateTime?)null).Should().Be("Unknown age");
        RelativeTime.Format(TimeSpan.MaxValue).Should().Be("Unknown age");
    }

    [Theory]
    [InlineData(0, "0m ago")]            // sub-minute floors to 0m
    [InlineData(59, "59m ago")]          // last value before the hour boundary
    [InlineData(60, "1h ago")]           // exactly one hour flips to hours
    [InlineData(24 * 60, "1d ago")]      // exactly one day flips to days
    [InlineData(45 * 24 * 60, "45d ago")]
    public void TimeSpan_FormatsWithFlooredUnits(int totalMinutes, string expected)
    {
        RelativeTime.Format(TimeSpan.FromMinutes(totalMinutes)).Should().Be(expected);
    }

    [Fact]
    public void TimeSpan_JustUnderBoundary_StaysInSmallerUnit()
    {
        RelativeTime.Format(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59)).Should().Be("59m ago");
        RelativeTime.Format(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)).Should().Be("23h ago");
    }
}

public class RelativeTimeFormatDetailedTests
{
    [Theory]
    [InlineData(0, 0, 0, 59, "just now")]   // under a minute
    [InlineData(0, 0, 1, 0, "1m ago")]      // exactly one minute leaves "just now"
    [InlineData(0, 1, 0, 0, "1h 0m ago")]   // exactly one hour flips to h+m form
    [InlineData(1, 0, 0, 0, "1d 0h ago")]   // exactly one day flips to d+h form
    public void FormatDetailed_ProducesExactStrings(int days, int hours, int minutes, int seconds, string expected)
    {
        var age = new TimeSpan(days, hours, minutes, seconds);

        RelativeTime.FormatDetailed(age).Should().Be(expected);
    }

    [Fact]
    public void FormatDetailed_HourForm_UsesRemainderMinutesNotTotal()
    {
        // 1h 5m must render the 5-minute remainder, not the 65 total minutes.
        RelativeTime.FormatDetailed(TimeSpan.FromMinutes(65)).Should().Be("1h 5m ago");
    }
}
