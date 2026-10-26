using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The Azure dashboard's client-side projections — the first tests they have ever had. They
/// were <c>private static</c> members of the <c>AzureDashboard</c> component until they moved
/// to <see cref="DashboardDerivations"/>, so ~460 lines of impact scoring, actionability
/// tiering and cleanup reasoning shipped to every visitor untested, even though this project
/// has always referenced the client assembly.
///
/// <para>Five whole-contract tests rather than one per property, and no <c>[Theory]</c> where
/// a table of asserts says the same thing: the tier budget is a ceiling (CLAUDE.md), and
/// making room for these meant collapsing <c>ReverseChronoRowKeyTests</c>, so they have to
/// earn their place. Each test drives one builder with one representative report and asserts
/// the whole shape that comes back.</para>
/// </summary>
public class DashboardDerivationsTests
{
    private static WebService Service(
        string name,
        string status = "active",
        int requests = 100,
        int http5xx = 0,
        string friendly = "",
        string type = "Microsoft.Web/sites",
        string rg = "PoThing") => new()
        {
            Name = name,
            FriendlyName = friendly,
            ResourceGroup = rg,
            ResourceType = type,
            HttpStatus = status,
            Metrics7Days = new MetricsInfo { Requests = requests, Http5xx = http5xx },
        };

    [Fact]
    public void ToActionability_TiersOnStatusThenErrorsThenTraffic()
    {
        // Down is urgent whether or not anyone is watching…
        DashboardDerivations.ToActionability("broken", 0, 100).Should().Be("Fix Now");
        // …and so is erroring hard while serving.
        DashboardDerivations.ToActionability("active", 11, 100).Should().Be("Fix Now");
        DashboardDerivations.ToActionability("active", 1, 100).Should().Be("Fix Soon");
        // Not active, but people are hitting it — worth a look, not a deletion.
        DashboardDerivations.ToActionability("other", 0, 100).Should().Be("Fix Soon");
        // Not active and nobody is: this is the one shape that earns "Remove Candidate".
        DashboardDerivations.ToActionability("other", 0, 0).Should().Be("Remove Candidate");
        DashboardDerivations.ToActionability("active", 0, 100).Should().Be("Watch");

        // The sortable form of the same ladder. Unknown sorts LAST — a tier nobody named is
        // not an emergency — which is what stops a typo reaching the top of the queue.
        new[] { "Watch", "Fix Soon", null, "Remove Candidate", "Fix Now" }
            .OrderBy(DashboardDerivations.ActionabilityRank)
            .Should().Equal("Fix Now", "Fix Soon", "Remove Candidate", "Watch", null);
    }

    [Fact]
    public void BuildPriorityQueue_OrdersByTierThenImpact_AndDeduplicatesOnSourceAndItem()
    {
        // A high-impact "Fix Soon" (TLS 1.0 storage, impact 72) against a lower-impact
        // "Fix Now" (a broken app), plus one certificate that appears twice. Ordering by
        // impact alone put the Fix Soon on top, so the badge and the ranking contradicted
        // each other and the reader had to pick which one to believe.
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo { Services = [Service("app-down", status: "broken", requests: 5)] },
            StorageInventory =
            [
                new StorageItem
                {
                    Name = "potrafficstorage2",
                    ResourceGroup = "potraffic",
                    HttpsOnly = true,
                    PublicBlobAccess = false,
                    IssueCount = 1,
                    Issues = [new StorageIssue { Issue = "Min TLS TLS1_0", Severity = "high" }],
                },
            ],
            SslExpiry =
            [
                new SslEntry { Name = "shared-cert", DaysLeft = 40 },   // Fix Soon, impact 65
                new SslEntry { Name = "shared-cert", DaysLeft = 3 },    // Fix Now,  impact 90
            ],
            // Advisor ranks into the same queue, one row per recommendation: High Cost/Security
            // is Fix Soon, everything else Watch, Low dropped.
            AdvisorRecommendations =
            [
                new AdvisorRecommendation { Category = "Cost", Impact = "High", Problem = "Right-size", ImpactedName = "plan-a", MonthlySavings = 12.5 },
                new AdvisorRecommendation { Category = "HighAvailability", Impact = "High", Problem = "Add zones", ImpactedName = "app-1" },
                new AdvisorRecommendation { Category = "HighAvailability", Impact = "High", Problem = "Add zones", ImpactedName = "app-2" },
                new AdvisorRecommendation { Category = "Security", Impact = "Low", Problem = "Nice to have", ImpactedName = "low-one" },
            ],
        };

        var consolidated = DashboardDerivations.BuildConsolidatedServices(report);
        var queue = DashboardDerivations.BuildPriorityQueue(report, consolidated, safe: []);

        queue.Select(i => DashboardDerivations.ActionabilityRank(i.Actionability))
            .Should().BeInAscendingOrder("the badge is the claim and the order must agree with it");
        queue[0].Actionability.Should().Be("Fix Now");
        queue.Should().Contain(i => i.Item == "potrafficstorage2" && i.Actionability == "Fix Soon");

        // Same source, same item — one row survives, and it is the worse of the two.
        queue.Where(i => i.Item == "shared-cert").Should().ContainSingle()
            .Which.ImpactScore.Should().Be(90);

        queue.Should().ContainSingle(i => i.Source == "Advisor · Cost")
            .Which.Should().Match<PriorityQueueItem>(i => i.Actionability == "Fix Soon" && i.Reason.Contains("$12.50/mo"));
        queue.Should().ContainSingle(i => i.Source == "Advisor · HighAvailability")
            .Which.Should().Match<PriorityQueueItem>(i => i.Actionability == "Watch" && i.Reason.StartsWith("2 resources: app-1, app-2"));
        queue.Should().NotContain(i => i.Item == "Nice to have");
    }

    [Fact]
    public void BuildSafeToRemove_RequiresBothAFaultAndZeroTraffic()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services =
                [
                    Service("app-broken-idle", status: "broken", requests: 0),
                    Service("app-broken-busy", status: "broken", requests: 4_000),
                    Service("app-fine-idle", status: "active", requests: 0),
                ],
            },
        };

        var safe = DashboardDerivations.BuildSafeToRemove(report);

        // A broken app people are still hitting is an outage to fix, never a deletion
        // candidate; an idle healthy app is simply idle. Only the intersection qualifies.
        safe.Select(s => s.Name).Should().Equal("app-broken-idle");
        safe[0].Confidence.Should().Be("high");
        safe[0].Command.Should().Contain("az webapp delete").And.Contain("app-broken-idle");
        safe[0].Reason.Should().Contain("0 requests in 7 days");

        DashboardDerivations.BuildSafeToRemove(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildSafeToRemove_DedupesByResourceAndKeepsTheHighestConfidenceVerdict()
    {
        // Same Microsoft.Web/sites flagged by two scans: connectivity + metrics (high,
        // because HttpStatus == "broken") AND the orphan scan (medium). The queue must keep
        // the high-confidence verdict, not whichever scan ran last.
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = [Service("app-broken-orphan", status: "broken", requests: 0)],
            },
            OrphanedResources =
            [
                new OrphanedResource
                {
                    Name = "app-broken-orphan",
                    ResourceGroup = "PoThing",
                    Type = "Microsoft.Web/sites",
                    Reason = "Orphan by tag scan",
                    EstimatedMonthlyCost = "$0/mo",
                    Command = "az webapp delete --name \"app-broken-orphan\" --resource-group \"PoThing\"",
                },
            ],
        };

        var safe = DashboardDerivations.BuildSafeToRemove(report);

        safe.Should().ContainSingle("the same resource must not appear twice");
        safe[0].Confidence.Should().Be("high", "the dedup must keep the higher-confidence verdict, not the last-written one");
        safe[0].Source.Should().Be("Connectivity + Metrics");
    }

    [Fact]
    public void BuildSafeToRemove_SurfacesCatalogDriftAsACleanupCandidateWithARemovalSnippet()
    {
        // Two catalog entries drift in different ways: the URL probe failed (medium
        // confidence — the host could be transiently down) and the entry exists in apps.json
        // but has no Microsoft.Web/sites in the subscription (high confidence — the
        // operator should rename or remove). The dashboard surfaces both as cleanup
        // candidates; we do NOT silently edit the catalog because a probe failure must
        // never blank a card.
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo { Services = [Service("app-polocalcompare", friendly: "PoLocalCompare")] },
            CatalogDrift =
            [
                new CatalogDriftItem
                {
                    Name = "PoMarriedLife",
                    Url = "https://pomarriedlife-api.azurewebsites.net",
                    Kind = "url-not-loadable",
                    Reason = "Probe to https://pomarriedlife-api.azurewebsites.net returned HTTP 404.",
                    Confidence = "medium",
                    RemovalSnippet = "{\n  \"id\": \"pomarriedlife-api\",\n  \"name\": \"PoMarriedLife\"\n}",
                },
                new CatalogDriftItem
                {
                    Name = "PoGhost",
                    Url = "https://ghost.example.com",
                    Kind = "missing-in-azure",
                    Reason = "No Microsoft.Web/sites found in this subscription matching 'PoGhost'.",
                    Confidence = "high",
                    RemovalSnippet = "{ id: ghost }",
                },
            ],
        };

        var safe = DashboardDerivations.BuildSafeToRemove(report);

        var married = safe.SingleOrDefault(i => i.Type == "Catalogue" && i.Name == "PoMarriedLife");
        married.Should().NotBeNull("apps.json drift findings must appear in the cleanup queue");
        married!.Source.Should().Be("apps.json drift");
        married.Confidence.Should().Be("medium", "a non-loading URL is medium confidence — the host could be transiently down");
        married.Command.Should().Be(report.CatalogDrift![0].RemovalSnippet,
            "the Command field carries the JSON snippet so the dashboard's existing Copy action works for it");
        married.Reason.Should().Contain("404", "the operator wants to know why this was flagged");

        var ghost = safe.Single(i => i.Name == "PoGhost");
        ghost.Confidence.Should().Be("high", "an Azure-less entry is more concerning than a transient probe failure");
        ghost.Source.Should().Be("apps.json drift");
    }

    [Fact]
    public void BuildConsolidatedServices_RollsUpOneAppsResourcesIntoOneRow_AndInfersItsOwnership()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services =
                [
                    Service("app-pothing", friendly: "PoThing", requests: 300, http5xx: 2, rg: "rg-prod-east"),
                    Service("pothing-static", friendly: "PoThing", requests: 200, http5xx: 1,
                            type: "Microsoft.Web/staticSites", rg: "rg-prod-east"),
                ],
            },
        };

        var rolled = DashboardDerivations.BuildConsolidatedServices(report);

        rolled.Should().ContainSingle("an app with a Web App and a Static Web App is one row, not two");
        rolled[0].DisplayName.Should().Be("PoThing");
        rolled[0].Requests7d.Should().Be(500);
        rolled[0].Http5xx7d.Should().Be(3);
        rolled[0].ResourceTypeSummary.Should().Contain("App Service").And.Contain("Static Web App");
        rolled[0].ReliabilityScore.Should().Be(91, "3 x 5xx costs 9 points and nothing else here is wrong");
        rolled[0].Owner.Should().Be("rg");
        rolled[0].Environment.Should().Be("prod");

        // The inference helpers at their edges: markers are read from either half of the
        // pair, and an empty pair must not produce an empty-string owner.
        DashboardDerivations.InferEnvironment("PoThing", "app-staging").Should().Be("dev");
        DashboardDerivations.InferEnvironment("poshared", "app-pothing").Should().Be("shared");
        DashboardDerivations.InferOwner(null, null).Should().Be("unassigned");
    }

    [Fact]
    public void BuildResourceExplorerItems_FlagsEveryConcernAndSortsRiskyRowsFirst()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services =
                [
                    Service("app-sick", status: "broken", requests: 0, friendly: "PoSick"),
                    Service("app-well", status: "active", requests: 10, friendly: "PoWell"),
                ],
            },
            StorageInventory =
            [
                new StorageItem { Name = "leakystorage", ResourceGroup = "rg", IssueCount = 1, PublicBlobAccess = true },
            ],
            AllResourceSummary = new AllResourceSummaryInfo
            {
                Total = 3,
                ResourcesByType = new()
                {
                    ["Microsoft.Web/sites"] =
                    [
                        new ResourceDetail { Name = "app-sick", ResourceGroup = "rg", Type = "Microsoft.Web/sites" },
                        new ResourceDetail { Name = "app-well", ResourceGroup = "rg", Type = "Microsoft.Web/sites" },
                    ],
                    ["Microsoft.Storage/storageAccounts"] =
                    [
                        new ResourceDetail { Name = "leakystorage", ResourceGroup = "rg", Type = "Microsoft.Storage/storageAccounts" },
                    ],
                },
            },
        };

        var consolidated = DashboardDerivations.BuildConsolidatedServices(report);
        var safe = DashboardDerivations.BuildSafeToRemove(report);
        var rows = DashboardDerivations.BuildResourceExplorerItems(report, consolidated, safe);

        rows.Should().HaveCount(3);
        rows.Last().Risk.Should().Be("None", "clean rows sort below every flagged one");

        rows.Single(r => r.Name == "PoSick").Risk.Should().Contain("Unhealthy").And.Contain("Waste");
        var leaky = rows.Single(r => r.Name == "leakystorage");
        leaky.Risk.Should().Contain("Security");
        leaky.Type.Should().Be("Storage Accounts");

        DashboardDerivations.BuildResourceExplorerItems(null, [], []).Should().BeEmpty();
    }

    [Fact]
    public void UptimeRow_IsOneSentence_NamingBadDaysAndMatchingAppsAcrossSpellings()
    {
        // The sentence is the uptime grid's whole accessible rendering AND the app drawer's
        // visible prose, so it must name the bad days, cap them at four, and say "1 check"
        // rather than the "1 check(s)" it used to print.
        var days = Enumerable.Range(0, 6).Select(i => new DateTime(2026, 10, 14 + i)).ToList();
        var row = new UptimeRow
        {
            Name = "PoSeeReview",
            Cells = [UptimeState.Broken, UptimeState.Broken, UptimeState.Degraded, UptimeState.Broken, UptimeState.Broken, UptimeState.Healthy],
            UptimePercent = 76,
            ScansObserved = 1,
            DaysWithData = 6,
            Errors24h = 3,
            TopError = "HttpRequestException",
        };

        DashboardDerivations.DescribeUptimeRow(row, days).Should().Be(
            "PoSeeReview: healthy on 76 percent of 1 check across 6 observed days of 6. "
            + "3 exceptions in the last 24 hours, mostly HttpRequestException. "
            + "Bad days: Oct 14 unavailable, Oct 15 unavailable, Oct 16 degraded, Oct 17 unavailable, and 1 more.");

        // ?focus=, the uptime row and the /users roster spell the same app three ways.
        DashboardDerivations.AppMatchKey("po-see review").Should().Be(DashboardDerivations.AppMatchKey("PoSeeReview"));
    }
}
