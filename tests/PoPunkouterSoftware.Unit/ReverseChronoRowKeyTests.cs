using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// Two whole-property tests over one two-line method. This was six cases — a pair proving
/// newer-sorts-first, a three-timestamp sort proving the same thing, a two-case width theory
/// and a MaxValue fact — i.e. the "one-assertion Facts that each re-fetch the same document"
/// shape docs/architecture.md describes, spending six tests' worth of a 100-test budget on one rule's
/// worth of coverage. They collapse without losing a single boundary, which is what made room
/// for <see cref="DashboardDerivationsTests"/>.
/// </summary>
public class ReverseChronoRowKeyTests
{
    [Fact]
    public void AscendingLexicalSort_YieldsNewestFirst_DownToASingleTick()
    {
        var timestamps = new[]
        {
            new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero),
        };

        // Table Storage returns rows in ascending lexical RowKey order, so the newer report
        // must produce the ordinally smaller key to come back first.
        timestamps
            .Select(t => (Key: new ReverseChronoRowKey(t).ToString(), At: t))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.At)
            .Should().BeInDescendingOrder();

        // The resolution floor: two saves one tick apart must still be distinct and ordered,
        // or a same-instant pair silently collapses under Upsert.
        var at = new DateTimeOffset(2026, 7, 9, 15, 30, 0, TimeSpan.Zero);
        var older = new ReverseChronoRowKey(at).ToString();
        var newer = new ReverseChronoRowKey(at.AddTicks(1)).ToString();
        newer.Should().NotBe(older);
        string.CompareOrdinal(newer, older).Should().BeNegative();
    }

    [Fact]
    public void RowKey_IsAlwaysTwentyDigits_AcrossTheWholeRepresentableRange()
    {
        // Fixed width is what makes lexical ordering equal numeric ordering; a key that
        // narrows by one digit sorts into the wrong place forever.
        foreach (var at in new[]
        {
            new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero),      // largest inverse value
            new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), // smallest inverse value
        })
        {
            new ReverseChronoRowKey(at).ToString().Should().MatchRegex("^[0-9]{20}$");
        }

        new ReverseChronoRowKey(DateTimeOffset.MaxValue).ToString().Should().Be(new string('0', 20));
    }
}
