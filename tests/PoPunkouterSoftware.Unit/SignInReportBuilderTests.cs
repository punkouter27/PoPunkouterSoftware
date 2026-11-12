using System.Text.Json;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The whole <c>/users</c> projection in one contract assertion, rather than a Fact per
/// field — the style CLAUDE.md asks for, and the reason this new slice costs the Unit tier
/// one test method instead of a dozen.
/// </summary>
public class SignInReportBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Shaped on the live Application Insights rows, including the two facts that make this
    /// projection non-trivial: the same person arriving under two different object ids
    /// (punkouter26 signed in to PoTraffic and PoWatch from separate app registrations), and
    /// providers that send the email address as the display name.
    /// </summary>
    private static readonly SignInRecord[] Records =
    [
        new(new DateTime(2026, 9, 12, 11, 1, 0, DateTimeKind.Utc), "PoTraffic", "uid-a",
            "yannick@lotus-diffusers.nl", "yannick@lotus-diffusers.nl", "microsoft"),
        new(new DateTime(2026, 9, 11, 3, 9, 0, DateTimeKind.Utc), "PoTraffic", "uid-b",
            "punkouter26@gmail.com", "punkouter26@gmail.com", "microsoft"),
        new(new DateTime(2026, 9, 9, 13, 35, 0, DateTimeKind.Utc), "PoWatch", "uid-c",
            "punkouter26@gmail.com", "Matthew Herb", "microsoft"),
        new(new DateTime(2026, 9, 11, 3, 3, 0, DateTimeKind.Utc), "PoRedoImage", "uid-d",
            "punkouter27@outlook.com", "matthew herb", "microsoft"),
        new(new DateTime(2026, 9, 11, 2, 52, 0, DateTimeKind.Utc), "PoLocalCompare", "uid-d",
            "punkouter27@outlook.com", "matthew herb", "microsoft"),
    ];

    [Fact]
    public void Build_GroupsPeopleByAddress_MasksEveryAddress_AndFillsTheTrendGrid()
    {
        var report = SignInReportBuilder.Build(Records, Now, windowDays: 7, truncated: false);

        report.Available.Should().BeTrue();
        report.Unavailable.Should().BeNull();
        report.WindowDays.Should().Be(7);
        report.TotalSignIns.Should().Be(5);
        report.TotalApps.Should().Be(4);

        // Three PEOPLE from five sign-ins across four object ids. Grouping on UserId — which
        // the "Po Sign-ins" workbook does — would report four, splitting punkouter26 in half.
        report.TotalPeople.Should().Be(3);
        report.People.Should().HaveCount(3);

        // Newest sign-in first.
        report.People[0].DisplayName.Should().Be("ya****k@lotus-diffusers.nl",
            because: "a provider that sends the address AS the display name must not defeat the mask");

        var merged = report.People.Single(p => p.MaskedEmail == "pu********6@gmail.com");
        merged.SignIns.Should().Be(2);
        merged.Apps.Should().Equal("PoTraffic", "PoWatch");
        merged.Providers.Should().Equal("microsoft");
        merged.FirstSeen.Should().Be(new DateTime(2026, 9, 9, 13, 35, 0, DateTimeKind.Utc));
        merged.LastSeen.Should().Be(new DateTime(2026, 9, 11, 3, 9, 0, DateTimeKind.Utc));
        merged.DisplayName.Should().Be("Matthew Herb",
            because: "a real name beats the same person's address-as-name rows");

        // Reach: widest first, and "people" is distinct humans, not distinct rows.
        report.Apps[0].App.Should().Be("PoTraffic");
        report.Apps[0].People.Should().Be(2);
        report.Apps[0].SignIns.Should().Be(2);
        report.Apps.Select(a => a.App).Should().BeEquivalentTo(
            ["PoTraffic", "PoWatch", "PoRedoImage", "PoLocalCompare"]);

        // One dense series per app: every day in the window is present on every series, so the
        // chart shares one category axis and a quiet day draws as a zero rather than a gap.
        report.Trend.Should().HaveCount(4);
        report.Trend.Should().OnlyContain(s => s.Points.Count == 7);
        report.Trend.Should().OnlyContain(s => s.Points[0].Day == new DateTime(2026, 9, 6));
        report.Trend.Should().OnlyContain(s => s.Points[6].Day == new DateTime(2026, 9, 12));
        report.Trend.Single(s => s.App == "PoTraffic").Points
            .Single(p => p.Day == new DateTime(2026, 9, 12)).SignIns.Should().Be(1);
        report.Trend.Single(s => s.App == "PoWatch").Points
            .Single(p => p.Day == new DateTime(2026, 9, 12)).SignIns.Should().Be(0);

        report.Recent.Should().HaveCount(5);
        report.Recent[0].App.Should().Be("PoTraffic");
        report.Recent.Should().BeInDescendingOrder(e => e.Timestamp);

        // The privacy contract, asserted over the WHOLE serialized payload rather than field
        // by field: /api/signins is anonymous on a site with no login, so a raw address
        // reaching the wire through any field — including one added later — is the failure
        // this test exists to catch.
        var json = JsonSerializer.Serialize(report);
        foreach (var address in new[]
                 { "punkouter26@gmail.com", "punkouter27@outlook.com", "yannick@lotus-diffusers.nl" })
        {
            json.Should().NotContain(address);
        }

        json.Should().Contain("gmail.com", because: "the domain is kept — it is the half that carries signal");

        // Cost per person joins on the stored scan: app name -> scanned service -> its own
        // resource group's spend. A group missing from the cost series spent $0 (Cost
        // Management omits zero rows); an app matching no scanned service stays unknown.
        var inventory = new PoPunkouterSoftware.Shared.AzureReport
        {
            WebServices = new()
            {
                Services =
            [
                new() { Name = "app-potraffic", FriendlyName = "PoTraffic", ResourceGroup = "rg-traffic" },
                new() { Name = "app-powatch", FriendlyName = "", ResourceGroup = "rg-watch" },
            ]
            },
            Cost = new() { ResourceGroups = [new() { Name = "RG-TRAFFIC", Total = 6.0 }] },
        };
        var costed = SignInReportBuilder.AttachCosts(report, inventory).Apps.ToDictionary(a => a.App);
        costed["PoTraffic"].Cost30Days.Should().Be(6.0);
        costed["PoTraffic"].CostPerPerson.Should().Be(3.0, because: "two people signed in to PoTraffic");
        costed["PoWatch"].Cost30Days.Should().Be(0, because: "matched by resource name; its group spent nothing");
        costed["PoRedoImage"].Cost30Days.Should().BeNull(because: "no scanned service matches it");
    }

    /// <summary>
    /// "No data" and "cannot read the data" are different answers and the page renders them
    /// differently, so the builder must keep them apart rather than collapsing both to empty.
    /// </summary>
    [Fact]
    public void EmptyIsAvailable_ButAFailureCarriesItsReason()
    {
        var empty = SignInReportBuilder.Build([], Now, windowDays: 30, truncated: false);

        empty.Available.Should().BeTrue(because: "nobody signing in is a real answer, not a fault");
        empty.Unavailable.Should().BeNull();
        empty.TotalPeople.Should().Be(0);
        empty.Trend.Should().BeEmpty();

        var broken = SignInReportBuilder.Unavailable(Now, windowDays: 30, "no Monitoring Reader role");

        broken.Available.Should().BeFalse();
        broken.Unavailable.Should().Be("no Monitoring Reader role");
        broken.People.Should().BeEmpty();
        broken.GeneratedAt.Should().Be(Now.UtcDateTime);
    }
}
