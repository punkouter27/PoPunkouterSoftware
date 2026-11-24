using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// <c>POST /api/visit</c>: records one anonymous visit as the fleet's canonical
/// <c>UserSignedIn</c> log record, so this login-free site shows on /users beside the apps that
/// do sign people in (owner's ask, 2026-11-23). PoMiniGames and PoMode carry the same file.
/// <para>The browser (<c>js/visit.js</c>) keeps a random id in localStorage and calls this at
/// most once per day, so "sign-ins" for an anonymous visitor reads as "days visited". Nothing
/// identifying is recorded: no email, no IP, no user agent — a random id, and 3-letter
/// initials only where the app asks for them (this site does not).</para>
/// </summary>
internal static partial class VisitEndpoints
{
    /// <summary>The cross-app query filters on this exact value (SignInQueryService).</summary>
    internal const string EventName = "UserSignedIn";
    internal const string AppName = "PoPunkouterSoftware";
    private static readonly EventId VisitEventId = new(6002, EventName);

    internal sealed record VisitRequest(string? VisitorId, string? Initials);

    /// <summary>What one visit is recorded as on /users. Null = reject the request.</summary>
    internal sealed record VisitRecord(string UserId, string DisplayName, string Provider);

    internal static WebApplication MapVisitEndpoints(this WebApplication app)
    {
        app.MapPost("/api/visit", (VisitRequest body, ILoggerFactory loggers) =>
            {
                if (Describe(body.VisitorId, body.Initials) is not { } visit)
                    return Results.BadRequest();
                Track(loggers.CreateLogger("PoPunkouterSoftware.Visits"), visit);
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
    internal static VisitRecord? Describe(string? visitorId, string? initials)
    {
        // TODO(human): validate both inputs and build the record.
        throw new NotImplementedException();
    }

    [GeneratedRegex("^[a-f0-9]{16,32}$")]
    internal static partial Regex VisitorIdShape();

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
            // customDimensions key the /users query projects is present.
            logger.LogInformation(VisitEventId,
                "{EventName} app={AppName} user={UserId} email={Email} name={DisplayName} tenant={TenantId} provider={Provider}",
                EventName, AppName, visit.UserId, "", visit.DisplayName, "(none)", visit.Provider);
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
