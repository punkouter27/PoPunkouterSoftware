using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// The <c>/api/global/*</c> surface: signed-in only, and reading the committed snapshot.
/// The unit tier proves the files parse; this tier proves the host wires them up behind the
/// same authentication boundary as /azure and /users.
/// </summary>
public class GlobalEndpointsTests(TestWebApp app) : IClassFixture<TestWebApp>
{
    private static readonly string[] Routes =
    [
        "/api/global/summary",
        "/api/global/fleet",
        "/api/global/vault",
        "/api/global/lessons",
        "/api/global/readiness",
        "/api/global/briefing",
        "/api/global/rl",
        "/api/global/queue",
        "/api/global/unite",
    ];

    private static HttpClient SignedIn(TestWebApp app)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Fake-User", "global-tests");
        return client;
    }

    [Theory]
    [InlineData("/api/global/summary")]
    [InlineData("/api/global/fleet")]
    [InlineData("/api/global/vault")]
    [InlineData("/api/global/lessons")]
    [InlineData("/api/global/readiness")]
    [InlineData("/api/global/briefing")]
    [InlineData("/api/global/rl")]
    [InlineData("/api/global/queue")]
    [InlineData("/api/global/unite")]
    public async Task GlobalEndpoints_RejectAnonymousRequests(string path)
    {
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_IsCopiedNextToTheApp()
    {
        var env = app.Services.GetRequiredService<IHostEnvironment>();
        var dir = Path.Combine(env.ContentRootPath, "App_Data", "global");

        Directory.Exists(dir).Should().BeTrue(
            $"the GLOBAL snapshot must be deployed with the app; looked in {dir}");
        File.Exists(Path.Combine(dir, "fleet.json")).Should().BeTrue();
    }

    [Fact]
    public async Task EveryGlobalRoute_AnswersSignedIn()
    {
        using var client = SignedIn(app);

        foreach (var route in Routes)
        {
            var response = await client.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{route} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Summary_ReportsTheCommittedFleet()
    {
        using var client = SignedIn(app);

        var summary = await client.GetFromJsonAsync("/api/global/summary", GlobalJsonContext.Default.GlobalSummaryDto);

        summary.Should().NotBeNull();
        summary!.RepoCount.Should().BeGreaterThan(0);
        summary.LessonsCount.Should().BeGreaterThan(0);
        summary.Generated.Should().NotBeNull();
    }

    [Fact]
    public async Task Fleet_ReturnsTheRepoList()
    {
        using var client = SignedIn(app);

        var fleet = await client.GetFromJsonAsync("/api/global/fleet", GlobalJsonContext.Default.GlobalFleetDto);

        fleet.Should().NotBeNull();
        fleet!.Repos.Should().NotBeEmpty();
        fleet.Repos.Should().OnlyContain(r => r.Name.Length > 0);
    }

    [Fact]
    public async Task Vault_GroupsBrainsBySocket()
    {
        using var client = SignedIn(app);

        var vault = await client.GetFromJsonAsync("/api/global/vault", GlobalJsonContext.Default.GlobalVaultDto);

        vault.Should().NotBeNull();
        vault!.Sockets.Should().NotBeEmpty();
        vault.Brains.Should().OnlyContain(b => b.Socket.Length > 0);
    }

    [Fact]
    public async Task Unite_ListsTheSixCreatures()
    {
        using var client = SignedIn(app);

        var unite = await client.GetFromJsonAsync("/api/global/unite", GlobalJsonContext.Default.GlobalUniteDto);

        unite.Should().NotBeNull();
        unite!.Creatures.Should().HaveCount(6);
    }

    [Fact]
    public async Task Queue_ListsTheTrainingJobsWithTheirCrawlCheck()
    {
        using var client = SignedIn(app);

        var queue = await client.GetFromJsonAsync("/api/global/queue", GlobalJsonContext.Default.GlobalQueueDto);

        queue.Should().NotBeNull();
        queue!.Generated.Should().NotBeNull();
        queue.Jobs.Should().NotBeEmpty();
        queue.Jobs.Should().OnlyContain(j => !string.IsNullOrEmpty(j.Id) && !string.IsNullOrEmpty(j.Command));
        queue.Jobs.Should().OnlyContain(j => j.Missing != null, "the crawl checks every job's requirements");
    }

    [Fact]
    public async Task Pages_RequireSignInAndRenderTheirHeading()
    {
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });
        var redirect = await anonymous.GetAsync("/global");
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirect.Headers.Location!.OriginalString.Should().Contain("/auth/login/microsoft");

        using var signedIn = SignedIn(app);
        var page = await signedIn.GetAsync("/global");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().Contain("Global");
    }
}
