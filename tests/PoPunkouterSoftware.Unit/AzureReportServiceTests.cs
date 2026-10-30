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
    public void AppErrors_SumPerApp_NameTheMostFrequentType_AndListTypesWithFirstAndLastSeen()
    {
        var t0 = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);
        var summary = AzureReportService.SummarizeAppErrors(
        [
            ("PoMode", "System.TimeoutException", 3, t0, t0.AddHours(5)),
            ("pomode", "System.NullReferenceException", 9, t0.AddHours(1), t0.AddHours(2)),
            ("PoWatch", "System.IO.IOException", 1, t0, t0),
            ("", "Orphan", 50, t0, t0),
            ("PoQuiet", "Ignored", 0, t0, t0),
        ]);

        summary.Select(s => (s.Count24h, s.TopType)).Should().Equal(
            (12, "System.NullReferenceException"),
            (1, "System.IO.IOException"));
        summary[0].Types.Select(t => (t.Type, t.Count, t.LastSeen)).Should().Equal(
            ("System.NullReferenceException", 9, t0.AddHours(2)),
            ("System.TimeoutException", 3, t0.AddHours(5)));
    }

    [Fact]
    public void PackagePriceAndDomainReaders_KeepOnlyWhatTheyCanJudge()
    {
        AzureReportService.ParseCentralPackages("""
            <Project><ItemGroup>
              <PackageVersion Include="Radzen.Blazor" Version="8.6.0" />
              <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="$(AspNetVersion)" />
            </ItemGroup></Project>
            """).Should().Equal(("Radzen.Blazor", "8.6.0"));
        AzureReportService.ParseCentralPackages("not xml").Should().BeEmpty();

        AzureReportService.IsNewer("12.1.8", "8.6.0").Should().BeTrue();
        AzureReportService.IsNewer("10.0.14", "10.0.14").Should().BeFalse();
        AzureReportService.IsNewer("2.0.0", "2.0.0-preview.1").Should().BeFalse("a prerelease pin is deliberate");

        AzureReportService.ParseTierPrices("""
            {"Items":[
              {"skuName":"B1","productName":"Azure App Service Basic Plan - Linux","unitOfMeasure":"1 Hour","retailPrice":0.018},
              {"skuName":"B1","productName":"Azure App Service Basic Plan","unitOfMeasure":"1 Hour","retailPrice":0.075},
              {"skuName":"P0v3","productName":"Azure App Service Premium v3 Plan","unitOfMeasure":"1 Hour","retailPrice":0.1575},
              {"skuName":"S1","productName":"Azure App Service Standard Plan","unitOfMeasure":"1/Month","retailPrice":9}
            ]}
            """, "westus2").Select(t => (t.Sku, t.Linux, t.MonthlyUsd)).Should().Equal(
            ("B1", true, 13.14), ("B1", false, 54.75), ("P0v3", false, 114.98));

        AzureReportService.RegistrableDomain("www.shop.example.com").Should().Be("example.com");
        AzureReportService.RegistrableDomain("app.example.co.uk").Should().Be("example.co.uk");
        AzureReportService.RegistrableDomain("localhost").Should().BeNull();
    }
}
