using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Fixed-window ceilings on the two anonymous endpoints that spend something on every call:
/// <c>POST /api/diag/ai</c> (a paid Azure AI Foundry completion) and <c>GET /api/signins</c>
/// (a live Log Analytics query, which Azure throttles per identity — a flood would lock the
/// page out for everyone). Both are anonymous by necessity: their only callers are WASM
/// islands with no credential. Nothing else needs a limiter; every other read is a projection
/// of the stored report.
///
/// <para>The limits are GLOBAL, not per client IP. That makes them a hard cost ceiling that no
/// amount of IP rotation gets around, and it avoids trusting X-Forwarded-For, which a caller
/// can set to anything. Both ceilings sit far above the owner's own use (the rewrite button is
/// clicked a few times a day; /users loads once per visit).</para>
/// </summary>
// ponytail: global windows, so one abuser can exhaust a window for every visitor until it
// resets. Partition by the App Service front end's X-Client-IP if that ever actually happens.
internal static class RateLimits
{
    public const string SignIns = "signins";
    public const string AiRewrite = "ai-rewrite";

    internal static IServiceCollection AddAppRateLimits(this IServiceCollection services, IConfiguration config) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };

            o.AddFixedWindowLimiter(SignIns, w =>
            {
                w.PermitLimit = config.GetValue("RateLimits:SignInsPerMinute", 30);
                w.Window = TimeSpan.FromMinutes(1);
                w.QueueLimit = 0;
            });
            o.AddFixedWindowLimiter(AiRewrite, w =>
            {
                w.PermitLimit = config.GetValue("RateLimits:AiRewritesPerHour", 20);
                w.Window = TimeSpan.FromHours(1);
                w.QueueLimit = 0;
            });
        });
}
