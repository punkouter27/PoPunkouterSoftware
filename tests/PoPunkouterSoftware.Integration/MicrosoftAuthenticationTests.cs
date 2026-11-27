using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PoPunkouterSoftware.Api;

namespace PoPunkouterSoftware.Integration;

internal static class SessionTestClient
{
    private const string SessionScheme = "PoPunkouterSoftware.Session";
    internal static HttpClient CreateSignedInClient(this WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SessionScheme);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test@example.invalid")],
            SessionScheme);
        var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            SessionScheme));
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}");
        return client;
    }
}

public class MicrosoftAuthenticationTests
{
    [Theory]
    [InlineData("/api/signins")]
    [InlineData("/api/diag/report")]
    [InlineData("/api/diag/summary")]
    [InlineData("/hubs/refresh/negotiate", true)]
    [InlineData("/api/diag/cancel-refresh", true)]
    public async Task PrivateEndpoints_RejectAnonymousRequests(string path, bool post = false)
    {
        await using var app = new ManagementGateApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = post ? await client.PostAsync(path, null) : await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Theory]
    [InlineData("/azure")]
    [InlineData("/users")]
    public async Task PrivatePages_RedirectToMicrosoft_WhilePortfolioStaysPublic(string path)
    {
        await using var app = new ManagementGateApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.OriginalString.Should().Contain("/auth/login/microsoft");
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var signedIn = app.CreateSignedInClient();
        var page = await signedIn.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().Contain("Sign out");
    }

    // The signed-in test session is test@example.invalid (SessionTestClient above).
    [Theory]
    [InlineData("TEST@example.invalid", true)]
    [InlineData("someone-else@example.invalid, TEST@example.invalid", true)]
    [InlineData("someone-else@example.invalid", false)]
    public async Task Session_IsHonouredOnlyForTheOwner(string ownerEmail, bool allowed)
    {
        await using var app = new ManagementGateApp(new() { ["MicrosoftAuth:OwnerEmail"] = ownerEmail });
        using var client = app.CreateSignedInClient();

        foreach (var api in new[] { "/api/global/summary", "/api/global/queue", "/api/signins", "/api/diag/report" })
        {
            var response = await client.GetAsync(api);
            if (allowed)
                response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, api);
            else
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, api);
        }

        foreach (var page in new[] { "/azure", "/users", "/global", "/global/queue" })
        {
            var response = await client.GetAsync(page);
            response.StatusCode.Should().Be(allowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page);
        }

        // A refused account sees the public site exactly as a signed-out visitor does.
        var home = await client.GetStringAsync("/");
        if (allowed)
            home.Should().Contain("Sign out").And.Contain("href=\"/global\"");
        else
            home.Should().Contain("Sign in with Microsoft").And.NotContain("href=\"/global\"")
                .And.NotContain("href=\"/azure\"").And.NotContain("href=\"/users\"");
    }

    [Fact]
    public async Task Production_WithNoOwnerConfigured_RefusesEverySession()
    {
        await using var app = new ManagementGateApp(new() { ["MicrosoftAuth:OwnerEmail"] = "" }, environment: "Production");
        using var client = app.CreateSignedInClient();

        (await client.GetAsync("/api/global/summary")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/azure")).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task SignedOutHome_OffersSignIn_AndHidesTheOwnerPages()
    {
        await using var app = new ManagementGateApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var home = await client.GetStringAsync("/");

        home.Should().Contain("Sign in with Microsoft");
        home.Should().NotContain("po-header__link");
    }

    [Fact]
    public async Task SignOut_RequiresAntiforgeryToken()
    {
        await using var app = new ManagementGateApp();
        using var client = app.CreateSignedInClient();
        (await client.PostAsync("/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var html = await client.GetStringAsync("/");
        var token = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token),
        });
        var logout = await client.PostAsync("/auth/logout", content);
        logout.StatusCode.Should().Be(HttpStatusCode.Redirect);
        logout.Headers.GetValues("Set-Cookie").Should().Contain(value => value.Contains("PoPunkouterSoftware.Auth=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MicrosoftLogin_UsesCodeAndPkce_AndRejectsExternalReturnUrl()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["MicrosoftAuth:ClientId"] = "test-client",
            ["MicrosoftAuth:ClientSecret"] = "test-only-not-a-real-secret",
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://login.microsoftonline.com/common/v2.0",
                    AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                    TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                })));
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var response = await client.GetAsync("/auth/login/microsoft?returnUrl=https://example.invalid");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("https://login.microsoftonline.com/")
            .And.Contain("response_type=code").And.Contain("code_challenge_method=S256");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location.Query);
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        options.StateDataFormat.Unprotect(query["state"])!.RedirectUri.Should().Be("/");
    }
}
