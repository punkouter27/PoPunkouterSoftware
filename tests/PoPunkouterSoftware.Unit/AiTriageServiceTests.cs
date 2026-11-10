using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class AiTriageServiceTests
{
    // ── BuildTemplateFallback — pure, no I/O ────────────────────────────────────

    [Fact]
    public void BuildTemplateFallback_NoItems_ReturnsAllClearMessage()
    {
        AiTriageService.BuildTemplateFallback(new List<string>())
            .Should().Be("No issues detected. No AI summary available.");
    }

    [Fact]
    public void BuildTemplateFallback_MixedItems_SummarizesEachCategoryAndSaysNoAi()
    {
        var items = new List<string>
        {
            "app-one is unavailable",
            "app-two is unavailable",
            "3 security configuration finding(s)",
            "2 cleanup candidate(s)",
        };

        var text = AiTriageService.BuildTemplateFallback(items);

        text.Should().Contain("3 security finding(s)");
        text.Should().Contain("2 cleanup candidate(s)");
        text.Should().Contain("2 service(s) unavailable");
        text.Should().EndWith("No AI summary available.");
    }

    [Fact]
    public void BuildTemplateFallback_StaleFlag_AddsStaleNote()
    {
        var items = new List<string> { "Azure data is stale and should be refreshed" };

        AiTriageService.BuildTemplateFallback(items).Should().Contain("Azure data may be stale.");
    }

    // ── ComputeAttentionHash — pure, no I/O ─────────────────────────────────────

    // One contract, one test (was three Facts, 2026-11-07): stable for equal input, different
    // for different input, and the lowercase SHA-256 hex the stored report carries.
    [Fact]
    public void ComputeAttentionHash_IsAStableLowercaseSha256OfTheItems()
    {
        var same1 = new List<string> { "x is unavailable", "3 security configuration finding(s)" };
        var same2 = new List<string> { "x is unavailable", "3 security configuration finding(s)" };
        AiTriageService.ComputeAttentionHash(same1).Should().Be(AiTriageService.ComputeAttentionHash(same2));

        AiTriageService.ComputeAttentionHash(new List<string> { "x is unavailable" })
            .Should().NotBe(AiTriageService.ComputeAttentionHash(new List<string> { "y is unavailable" }));

        AiTriageService.ComputeAttentionHash(new List<string> { "a" })
            .Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ── GenerateSummaryAsync — hash-comparison/caching orchestration ────────────
    // Exercised with an in-memory fake HttpMessageHandler (no real network call), which
    // keeps this within the Unit tier's "strictly no-I/O (HTTP stubs...)" rule while still
    // proving the cache-vs-call decision end to end.

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            // Azure OpenAI chat-completions shape. Trimmed to the two fields the service
            // reads; the live API also returns content filters, usage and ids.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"All systems normal."}}]}"""),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static AiTriageService CreateService(FakeHandler handler, bool enabled)
    {
        var httpClient = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(httpClient);
        // An API key is configured so the service never reaches for a TokenCredential:
        // acquiring an Entra token would be real I/O, which the Unit tier forbids.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:EnableAiSummary"] = enabled ? "true" : "false",
                ["StatusNarrator:Endpoint"] = "https://unit-test.cognitiveservices.azure.com/",
                ["StatusNarrator:Deployment"] = "test-deployment",
                ["StatusNarrator:ApiKey"] = "unit-test-key",
            })
            .Build();
        return new AiTriageService(factory, config, NullLogger<AiTriageService>.Instance);
    }

    [Fact]
    public async Task GenerateSummaryAsync_FirstScan_CallsTheModel_SourceIsAi()
    {
        var handler = new FakeHandler();
        var service = CreateService(handler, enabled: true);

        var result = await service.GenerateSummaryAsync(
            new List<string> { "app-one is unavailable" }, previous: null, CancellationToken.None);

        result.Source.Should().Be("ai");
        result.Text.Should().Be("All systems normal.");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateSummaryAsync_UnchangedAttentionItems_ReusesPreviousSummary_SkipsModelCall()
    {
        var handler = new FakeHandler();
        var service = CreateService(handler, enabled: true);
        var items = new List<string> { "app-one is unavailable" };

        var first = await service.GenerateSummaryAsync(items, previous: null, CancellationToken.None);
        var second = await service.GenerateSummaryAsync(items, previous: first, CancellationToken.None);

        second.Source.Should().Be("cached");
        second.Text.Should().Be(first.Text);
        handler.CallCount.Should().Be(1,
            because: "nothing material changed, so the second scan must not re-call the Azure AI model");
    }

    [Fact]
    public async Task GenerateSummaryAsync_ChangedAttentionItems_CallsTheModelAgain()
    {
        var handler = new FakeHandler();
        var service = CreateService(handler, enabled: true);

        var first = await service.GenerateSummaryAsync(
            new List<string> { "app-one is unavailable" }, previous: null, CancellationToken.None);
        var second = await service.GenerateSummaryAsync(
            new List<string> { "app-two is unavailable" }, previous: first, CancellationToken.None);

        second.Source.Should().Be("ai");
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GenerateSummaryAsync_PreviousWasItselfAFallback_StillCallsTheModel()
    {
        // A "template-fallback"/"disabled" previous summary must not be treated as a valid
        // cache entry even if the hash matches — only a real "ai" generation may be reused.
        var handler = new FakeHandler();
        var service = CreateService(handler, enabled: true);
        var items = new List<string> { "app-one is unavailable" };
        var hash = AiTriageService.ComputeAttentionHash(items);
        var fallbackPrevious = new PoPunkouterSoftware.Shared.AiSummaryResult
        {
            Text = AiTriageService.BuildTemplateFallback(items),
            Source = "template-fallback",
            AttentionHash = hash,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = await service.GenerateSummaryAsync(items, previous: fallbackPrevious, CancellationToken.None);

        result.Source.Should().Be("ai");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateSummaryAsync_Disabled_ReturnsNarrativeFallback_NeverCallsTheModel()
    {
        // The disabled path fills the same lead-paragraph slot the model output would, so it
        // returns a readable status narrative (BuildNarrativeFallback) rather than the
        // diagnostic "No AI summary available." note. That note still exists for the ad-hoc
        // attention-items path — see the BuildTemplateFallback tests above.
        var handler = new FakeHandler();
        var service = CreateService(handler, enabled: false);

        var facts = new StatusFacts
        {
            TotalServices = 4,
            ActiveServices = 3,
            BrokenServices = 1,
            HealthPercent = 75,
            BrokenServiceNames = new[] { "app-one" },
        };

        var result = await service.GenerateSummaryAsync(facts, previous: null, CancellationToken.None);

        result.Source.Should().Be("disabled");
        result.Text.Should().Contain("app-one").And.Contain("unavailable");
        result.Text.Should().NotContain("No AI summary available.",
            because: "the lead paragraph must read as a status sentence, not as a diagnostic about the AI feature");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public void NarrativeFallback_SaysAllClear_OrToRunARefreshWhenThereIsNoScan()
    {
        var healthy = new StatusFacts { TotalServices = 8, ActiveServices = 8, HealthPercent = 100 };
        AiTriageService.BuildNarrativeFallback(healthy).Should().Contain("All 8 apps are responding normally.");

        AiTriageService.BuildNarrativeFallback(new StatusFacts()).Should().Contain("Run a refresh");
    }

    // Both sides of the cache key's one rule: it moves on a fact that matters, and only then.
    [Fact]
    public void FactHash_IgnoresSubDollarCostDrift_ButChangesWhenAServiceGoesDown()
    {
        // Hashing raw cents would make every scan look "changed" and the model would be
        // re-called on every single refresh — the cache would never hit.
        var a = new StatusFacts { Cost30Days = 12.08 };
        var b = new StatusFacts { Cost30Days = 12.41 };
        AiTriageService.ComputeAttentionHash(a.ToHashInputs())
            .Should().Be(AiTriageService.ComputeAttentionHash(b.ToHashInputs()));

        var before = new StatusFacts { TotalServices = 4, ActiveServices = 4, HealthPercent = 100 };
        var after = new StatusFacts
        {
            TotalServices = 4,
            ActiveServices = 3,
            BrokenServices = 1,
            HealthPercent = 75,
            BrokenServiceNames = new[] { "app-one" },
        };

        AiTriageService.ComputeAttentionHash(before.ToHashInputs())
            .Should().NotBe(AiTriageService.ComputeAttentionHash(after.ToHashInputs()));
    }
}
