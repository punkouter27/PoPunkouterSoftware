using System.Net;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// <c>/api/global/reports/{name}</c>: GLOBAL's RL reports, served signed-in only, by allow-listed
/// name, with their own locked-down CSP (they carry inline script and are framed by /global/rl).
/// </summary>
public class GlobalReportsTests(TestWebApp app) : IClassFixture<TestWebApp>
{
    private HttpClient SignedIn()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Fake-User", "report-tests");
        return client;
    }

    [Theory]
    [InlineData("rl-overview")]
    [InlineData("rl-progress")]
    public async Task Reports_RejectAnonymousRequests(string name)
    {
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/global/reports/{name}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("rl-overview", "What Should I Train Next?")]
    [InlineData("rl-progress", "RL Agent Progress Over Time")]
    public async Task Reports_AreServedSandboxedAndFramableOnlyByThisSite(string name, string heading)
    {
        using var client = SignedIn();

        var response = await client.GetAsync($"/api/global/reports/{name}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        csp.Should().StartWith("default-src 'none'");
        csp.Should().Contain("sandbox allow-scripts").And.Contain("frame-ancestors 'self'")
            .And.NotContain("allow-same-origin").And.NotContain("connect-src");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("SAMEORIGIN");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain(heading);
        html.Should().NotContain("src=\"../", "local file paths are rewritten to GitHub");
        html.Should().NotContain("href=\"../", "local file paths are rewritten to GitHub or the site");
    }

    [Theory]
    [InlineData("fleet")]
    [InlineData("..%2Ffleet")]
    [InlineData("rl-overview.html")]
    [InlineData("RL-OVERVIEW")]
    public async Task OnlyTheNamedReportsAreServed(string name)
    {
        using var client = SignedIn();

        var response = await client.GetAsync($"/api/global/reports/{name}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OtherResponsesKeepTheSiteWideFramingBan()
    {
        using var client = SignedIn();

        var response = await client.GetAsync("/api/global/summary");

        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        string.Join(" ", response.Headers.GetValues("Content-Security-Policy")).Should().Contain("frame-ancestors 'none'");
    }
}
