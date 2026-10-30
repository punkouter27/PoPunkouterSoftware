using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

public class AlertTriggersTests
{
    /// <summary>
    /// Every rule fires on the crossing and stay quiet the night after: a channel that repeats
    /// yesterday's news is a channel that gets muted.
    /// </summary>
    [Fact]
    public void EveryRule_AlertsOnceWhenCrossed_NotEveryScanAfter()
    {
        var yesterday = new AzureReport
        {
            SslExpiry = [new SslEntry { Name = "a", Url = "https://a", DaysLeft = 15 }, new SslEntry { Name = "b", Url = "https://b", DaysLeft = 9 }],
            Cost = new CostInfo { TotalCost30Days = 30 },
        };
        var today = yesterday with
        {
            SslExpiry =
            [
                new SslEntry { Name = "a", Url = "https://a", DaysLeft = 14 },  // crossed tonight
                new SslEntry { Name = "b", Url = "https://b", DaysLeft = 8 },   // already announced
                new SslEntry { Name = "c", Url = "https://c", DaysLeft = 90 },  // nowhere near
            ],
            Cost = new CostInfo { TotalCost30Days = 45 },
        };

        AlertTriggers.NewSslExpiries(today, yesterday).Select(s => s.Name).Should().Equal("a");
        AlertTriggers.NewSslExpiries(today, today).Should().BeEmpty();

        AlertTriggers.BudgetCrossed(today, yesterday, budgetUsd: 40).Should().NotBeNull()
            .And.Match<CostForecast>(f => f.Verdict == CostVerdict.Over && f.ProjectedMonthTotal == 45);
        AlertTriggers.BudgetCrossed(today, today, budgetUsd: 40).Should().BeNull("it was already over");
        AlertTriggers.BudgetCrossed(today, yesterday, budgetUsd: null).Should().BeNull("no budget, no verdict");

        // "Up but wrong": a page that lost its expected text and a domain inside 30 days push
        // on the crossing too, and read as first-paint attention sentences. A check that could
        // not read the page (cold start, 5xx) claims nothing.
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        WebService Svc(string name, ContentCheckInfo? check, string? conclusion = null, int vulnerable = 0) => new()
        {
            Name = name, FriendlyName = name, HttpStatus = ServiceHealth.Active, ContentCheck = check,
            LastDeploy = conclusion is null ? null : new DeployInfo { Conclusion = conclusion },
            Packages = vulnerable == 0 ? null : new PackageHealthInfo { Checked = 9, Vulnerable = [.. Enumerable.Repeat("X 1.0.0 (GHSA-1)", vulnerable)] },
        };
        var before = new AzureReport
        {
            WebServices = new WebServicesInfo { Services =
            [
                Svc("A", new ContentCheckInfo { Expect = "<title>A</title>", Found = true }),
                Svc("B", new ContentCheckInfo { Expect = "<title>B</title>", Found = false }),
            ] },
            DomainExpiries = [new DomainExpiry { Domain = "old.com", ExpiresAt = now.AddDays(20) }],
        };
        var after = before with
        {
            WebServices = new WebServicesInfo { Services =
            [
                Svc("A", new ContentCheckInfo { Expect = "<title>A</title>", Found = false }, conclusion: "failure", vulnerable: 2),
                Svc("B", new ContentCheckInfo { Expect = "<title>B</title>", Found = false }),
                Svc("C", new ContentCheckInfo { Expect = "<title>C</title>", Error = "timeout" }),
            ] },
            DomainExpiries =
            [
                new DomainExpiry { Domain = "old.com", ExpiresAt = now.AddDays(19) },
                new DomainExpiry { Domain = "new.com", ExpiresAt = now.AddDays(29) },
                new DomainExpiry { Domain = "far.com", ExpiresAt = now.AddDays(300) },
            ],
        };

        AlertTriggers.NewContentFailures(after, before).Select(s => s.Name).Should().Equal("A");
        AlertTriggers.NewDomainExpiries(after, before, now).Select(d => d.Domain).Should().Equal("new.com");

        AttentionItemsBuilder.Build(after, _ => false).AttentionItems.Should().Contain(
        [
            "A answers, but its page no longer contains \"<title>A</title>\"",
            "A's latest build failed",
            "A pins 2 packages with a known vulnerability",
        ]).And.NotContain(i => i.StartsWith("C "), "a timeout is no verdict");
    }
}
