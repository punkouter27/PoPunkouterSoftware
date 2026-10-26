using System.Text.Json;
using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

// The pure readers behind two scan steps. The I/O around them (ARM, Log Analytics) is not
// reachable from this tier; the shape of what Azure returns is.
public class AzureReportServiceTests
{
    [Fact]
    public void AdvisorPage_ReadsEachRecommendation_SkipsRowsWithNoProblem_AndHidesTheSubscriptionId()
    {
        var page = JsonDocument.Parse("""
            {"value":[
              {"properties":{"category":"Cost","impact":"High","impactedField":"Microsoft.Web/serverFarms","impactedValue":"plan-a",
                "shortDescription":{"problem":"Right-size underused plans","solution":"Scale down"},
                "extendedProperties":{"savingsAmount":"12.499"}}},
              {"properties":{"category":"Security","impact":"Low","shortDescription":{}}},
              {"properties":{"category":"HighAvailability","impact":"High","impactedValue":"14d963a2-badc-4a1f-9900-553bf01f8de2",
                "shortDescription":{"problem":"Create a Service Health alert"}}}
            ],"nextLink":null}
            """).RootElement;

        var recs = AzureReportService.ReadAdvisorPage(page);

        recs.Should().HaveCount(2);
        recs[1].ImpactedName.Should().Be("subscription", "a bare subscription GUID would reach an anonymous endpoint");
        recs[0].Should().BeEquivalentTo(new
        {
            Category = "Cost", Impact = "High", Problem = "Right-size underused plans", Solution = "Scale down",
            ImpactedName = "plan-a", ImpactedType = "Microsoft.Web/serverFarms", MonthlySavings = 12.5,
        });
    }

    [Fact]
    public void AppErrors_SumPerApp_AndNameTheMostFrequentType()
    {
        var summary = AzureReportService.SummarizeAppErrors(
        [
            ("PoMode", "System.TimeoutException", 3),
            ("pomode", "System.NullReferenceException", 9),
            ("PoWatch", "System.IO.IOException", 1),
            ("", "Orphan", 50),
            ("PoQuiet", "Ignored", 0),
        ]);

        summary.Select(s => (s.Count24h, s.TopType)).Should().Equal(
            (12, "System.NullReferenceException"),
            (1, "System.IO.IOException"));
    }
}
