using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// <c>POST /api/visit</c>: records one anonymous visit as the fleet's canonical
/// <c>UserSignedIn</c> log record, so this login-free site shows on /users beside the apps that
/// do sign people in (owner's ask, 2026-11-23).
/// <para>The browser (<c>js/visit.js</c>) keeps a random id in localStorage and calls this at
/// most once per day, so "sign-ins" for an anonymous visitor reads as "days visited". Automated
/// browsers and crawlers are ignored. The full client IP is written only to Application Insights
/// (<c>ClientIp</c>) for private Log Analytics queries and is never projected to the public
/// <c>/api/signins</c> endpoint; only a coarse browser/OS + masked subnet summary
/// (<c>ClientInfo</c>) is surfaced on <c>/users</c>.</para>
/// </summary>
internal static partial class VisitEndpoints
{
    /// <summary>The cross-app query filters on this exact value (SignInQueryService).</summary>
    internal const string EventName = "UserSignedIn";
    internal const string AppName = "PoPunkouterSoftware";
    private static readonly EventId VisitEventId = new(6002, EventName);

    internal sealed record VisitRequest(string? VisitorId, string? Initials);

    /// <summary>What one visit is recorded as on /users. Null = reject the request.</summary>
    internal sealed record VisitRecord(
        string UserId,
        string DisplayName,
        string Provider,
        string ClientInfo = "",
        string ClientIp = "");

    internal static WebApplication MapVisitEndpoints(this WebApplication app)
    {
        app.MapPost("/api/visit", (
                VisitRequest body,
                HttpContext http,
                IWebHostEnvironment env,
                ILoggerFactory loggers) =>
            {
                if (Describe(body.VisitorId, body.Initials) is not { } visit)
                    return Results.BadRequest();

                var userAgent = http.Request.Headers.UserAgent.ToString();
                if (!env.IsProduction() || IsAutomatedAgent(userAgent))
                    return Results.NoContent();

                var ip = ResolveClientIp(http);
                var enriched = visit with
                {
                    ClientInfo = BuildClientInfo(userAgent, ip),
                    ClientIp = ip?.ToString() ?? "",
                };

                Track(loggers.CreateLogger("PoPunkouterSoftware.Visits"), enriched);
                return Results.NoContent();
            })
            .RequireRateLimiting(RateLimits.Visits)
            .WithName("RecordVisit").WithTags("Visits");
        return app;
    }

    /// <summary>
    /// Turns the browser's id and optional initials into the record /users shows.
    /// </summary>
    /// <param name="visitorId">Random per-browser id from localStorage (hex, untrusted).</param>
    /// <param name="initials">The visitor's initials where the app asks for them; null here.</param>
    /// <returns>The record, or null when either input is malformed (the endpoint answers 400).</returns>
    internal static VisitRecord? Describe(string? visitorId, string? initials)
    {
        // The id is the only thing that tells visitors apart, so it must be exactly the shape
        // js/visit.js makes; anything else is junk or a probe and is never logged.
        if (visitorId is null || !VisitorIdShape().IsMatch(visitorId))
            return null;

        // Initials are optional (this site never asks), but when sent they must be three letters.
        string? letters = null;
        if (!string.IsNullOrWhiteSpace(initials))
        {
            letters = initials.Trim().ToUpperInvariant();
            if (!InitialsShape().IsMatch(letters))
                return null;
        }

        // A short id slice keeps /users readable without exposing the whole browser id.
        var name = letters ?? $"Visitor {visitorId[..6]}";
        return new VisitRecord($"visitor:{visitorId}", name, "anonymous");
    }

    /// <summary>
    /// Filters out crawlers, link-previewers, CLI tools and headless test drivers so they do not
    /// flood /users with single-visit random ids.
    /// </summary>
    internal static bool IsAutomatedAgent(string? userAgent) =>
        !string.IsNullOrWhiteSpace(userAgent) && AutomatedAgentPattern().IsMatch(userAgent);

    /// <summary>
    /// Extracts the client IP from Azure App Service's <c>X-Forwarded-For</c> header (stripping
    /// the ephemeral <c>:port</c> suffix App Service appends) or falls back to the connection IP.
    /// </summary>
    internal static IPAddress? ResolveClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (TryParseIpWithOptionalPort(first, out var parsed))
                return NormalizeIp(parsed);
        }

        return http.Connection.RemoteIpAddress is { } remote ? NormalizeIp(remote) : null;
    }

    internal static bool TryParseIpWithOptionalPort(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        // Bracketed IPv6 with optional port: [2001:db8::1]:12345
        if (trimmed.StartsWith('['))
        {
            var end = trimmed.IndexOf(']');
            if (end > 1 && IPAddress.TryParse(trimmed[1..end], out var bracketed))
            {
                address = bracketed;
                return true;
            }
            return false;
        }

        if (IPAddress.TryParse(trimmed, out var direct))
        {
            address = direct;
            return true;
        }

        // IPv4 with port: 73.12.34.56:54321
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon && IPAddress.TryParse(trimmed[..colon], out var v4))
        {
            address = v4;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Masks an IP to a coarse network prefix safe for a public page: <c>73.12.*.*</c> for IPv4,
    /// first two hextets (<c>2601:19b:*</c>) for IPv6.
    /// </summary>
    internal static string MaskIp(IPAddress? ip)
    {
        if (ip is null)
            return "";

        ip = NormalizeIp(ip);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.*.*";
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            var h0 = (bytes[0] << 8) | bytes[1];
            var h1 = (bytes[2] << 8) | bytes[3];
            return $"{h0:x}:{h1:x}:*";
        }

        return "";
    }

    /// <summary>
    /// Turns a raw User-Agent header into a compact human-readable label (e.g. "Edge · Windows",
    /// "Safari · iPhone") without external dependencies.
    /// </summary>
    internal static string SummarizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "";

        var browser =
            userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("EdgA/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("EdgiOS/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : userAgent.Contains("OPR/", StringComparison.OrdinalIgnoreCase)
              || userAgent.Contains("Opera", StringComparison.OrdinalIgnoreCase) ? "Opera"
            : userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)
              || userAgent.Contains("FxiOS/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
              || userAgent.Contains("CriOS/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "";

        var os =
            userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)
              || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : userAgent.Contains("CrOS", StringComparison.OrdinalIgnoreCase) ? "ChromeOS"
            : userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "";

        return (browser, os) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{browser} · {os}",
            ({ Length: > 0 }, _) => browser,
            (_, { Length: > 0 }) => os,
            _ => "",
        };
    }

    internal static string BuildClientInfo(string? userAgent, IPAddress? ip)
    {
        var agent = SummarizeUserAgent(userAgent);
        var masked = MaskIp(ip);
        return (agent, masked) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{agent} · {masked}",
            ({ Length: > 0 }, _) => agent,
            (_, { Length: > 0 }) => masked,
            _ => "",
        };
    }

    private static IPAddress NormalizeIp(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    [GeneratedRegex("^[a-f0-9]{16,32}$")]
    internal static partial Regex VisitorIdShape();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex InitialsShape();

    [GeneratedRegex(
        @"bot\b|crawler|spider|headless|playwright|puppeteer|selenium|lighthouse|bingpreview|facebookexternalhit|slackbot|discordbot|whatsapp|twitterbot|linkedinbot|curl/|wget/|python-requests|go-http-client|uptime|pingdom|statuscake|monitor",
        RegexOptions.IgnoreCase)]
    private static partial Regex AutomatedAgentPattern();

    /// <summary>
    /// Writes the record OUTSIDE the request's trace. The Azure Monitor distro samples traces at
    /// 10% here and drops log records that belong to a sampled-out trace, so a record written
    /// inside the request would lose nine visits in ten. Never throws.
    /// </summary>
    private static void Track(ILogger logger, VisitRecord visit)
    {
        var current = Activity.Current;
        try
        {
            Activity.Current = null;
            // Same template and placeholder names as the fleet's SignInTelemetry, so every
            // customDimensions key the /users query projects is present. ClientIp is recorded
            // only for private Log Analytics inspection and is deliberately NOT projected by
            // SignInQueryService.
            logger.LogInformation(VisitEventId,
                "{EventName} app={AppName} user={UserId} email={Email} name={DisplayName} tenant={TenantId} provider={Provider} client={ClientInfo} ip={ClientIp}",
                EventName, AppName, visit.UserId, "", visit.DisplayName, "(none)", visit.Provider, visit.ClientInfo, visit.ClientIp);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Visit telemetry could not be recorded.");
        }
        finally
        {
            Activity.Current = current;
        }
    }
}
