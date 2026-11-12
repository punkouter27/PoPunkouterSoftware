namespace PoPunkouterSoftware.Api;

/// <summary>
/// Response security headers for every request, set in middleware because this app is
/// hosted on App Service and there is nowhere else for them to live.
///
/// <para>They used to live in <c>wwwroot/staticwebapp.config.json</c> — an Azure Static
/// Web Apps configuration file, in an app that has never been a Static Web App. Nothing
/// read it. The file declared a full CSP, <c>nosniff</c> and a Referrer-Policy, and looked
/// for all the world like the site was sending them; production sent none of the four.
/// A security control that exists only in an unread file is worse than no control, because
/// it stops anyone from noticing the gap.</para>
///
/// <para>The CSP is strict on scripts on purpose: <c>script-src 'self' 'wasm-unsafe-eval'</c>
/// with no <c>'unsafe-inline'</c>, which is why App.razor's boot and Blazor-hook scripts are
/// external files (js/app-boot.js, js/blazor-hooks.js) rather than inline blocks. Adding an
/// inline &lt;script&gt; anywhere in the host page will now silently stop executing — move it
/// to a file under wwwroot/js instead. <c>'wasm-unsafe-eval'</c> is required by the Blazor
/// WebAssembly runtime; <c>style-src</c> keeps <c>'unsafe-inline'</c> because Radzen and
/// Blazor both write inline style attributes.</para>
/// </summary>
internal static class SecurityHeaders
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "worker-src 'self' blob:; " +
        "manifest-src 'self'; " +
        "connect-src 'self'";

    internal static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // frame-ancestors above is the modern control; this is the fallback for
            // anything that still honours only the legacy header. DENY, not SAMEORIGIN:
            // nothing in this app frames anything.
            headers["X-Frame-Options"] = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            // X-Powered-By is NOT removed here on purpose: on App Service (Windows) IIS
            // appends it downstream of this pipeline, so a middleware Remove() is a no-op
            // that reads like a fix. web.config strips it at the server level instead.

            await next();
        });

        return app;
    }
}
