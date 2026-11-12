using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// NET_RULES §3 auth:
///   "Use FakeAuthHandler driven by X-Fake-User and X-Fake-Roles headers."
///   "FakeAuthHandler MUST throw an InvalidOperationException if initialized in a Production environment."
///
/// Reads two request headers:
///   X-Fake-User           — the username (any non-empty string; absent = anonymous "guest")
///   X-Fake-Roles          — comma-separated roles (e.g. "management,reader")
///
/// The handler is registered in Program.cs ONLY for non-Production environments
/// (Development, Testing). The constructor guard ensures the misuse is loud and
/// immediate: spinning the handler up in Production is a deployment error that
/// must never silently default-open mutating endpoints.
/// </summary>
public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Fake";

    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    public const string AnonymousUser = "guest";

    public const string ManagementRole = "management";

    public FakeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        // Guardrail: throw on accidental Production use. The handler is
        // wired only in Development/Testing, but a misconfigured environment
        // name (typo, missing ASPNETCORE_ENVIRONMENT) is the kind of bug
        // we want to catch at startup, not after a successful bypass.
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{nameof(FakeAuthHandler)} is for development and testing only. " +
                $"Refusing to initialize in environment '{environment.EnvironmentName}'. " +
                $"For Production, remove the FakeAuth scheme registration from Program.cs.");
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Default = anonymous guest. This matches NET_RULES's "no real auth"
        // stance: the site is publicly browsable; the headers only opt a
        // caller into elevated roles (e.g. management).
        var user = AnonymousUser;
        var roles = new List<string>();

        if (Request.Headers.TryGetValue(UserHeader, out var userHeader) && !string.IsNullOrWhiteSpace(userHeader))
        {
            user = userHeader.ToString();
        }

        if (Request.Headers.TryGetValue(RolesHeader, out var rolesHeader) && !string.IsNullOrWhiteSpace(rolesHeader))
        {
            roles.AddRange(rolesHeader
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(ClaimTypes.NameIdentifier, user),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
