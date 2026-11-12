using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// The read contract behind <c>/users</c>: who has signed in to any Po app, from the shared
/// Application Insights component. One endpoint, one consumer — the <c>SignIns</c> page.
/// </summary>
/// <remarks>
/// <para>Anonymous, like <c>/api/diag/report</c> and for the same reason: the page is a WASM
/// island with no credential, and this app deliberately has no identity provider. That is
/// precisely why the payload is masked before it leaves the server — see
/// <c>SecretMasking.MaskEmail</c>. The roster contains the addresses of real people,
/// including people who are not the owner, and an unmasked one would publish third-party PII
/// to any visitor.</para>
/// <para>Unprivileged rather than behind <c>.RequireManagementActions()</c>: it mutates
/// nothing and costs one Log Analytics query, which puts it alongside <c>/api/diag/ai</c>.
/// Being anonymous AND a live query is why it is rate-limited — see
/// <c>RateLimits</c>. A management gate would also make it unreachable from the page —
/// the WASM client cannot present the <c>X-Management-Key</c> header — leaving an endpoint
/// whose only caller is its own test, which is the exact shape CLAUDE.md forbids.</para>
/// </remarks>
internal static class SignInEndpoints
{
    internal static WebApplication MapSignInEndpoints(this WebApplication app)
    {
        app.MapGet("/api/signins", async (
            SignInQueryService signIns, AzureReportStore store, IWebHostEnvironment env,
            ILogger<Program> logger, int? days, CancellationToken ct) =>
        {
            // A caller-supplied window is clamped, never rejected: ?days=9999 means "as much
            // as you have", and answering 400 to it would be pedantry about a number the page
            // only offers three values for anyway.
            var window = days is > 0 ? SignInQueryService.ClampWindow(days.Value) : signIns.DefaultWindowDays;

            // Both reads degrade rather than throw — the sign-in service to an "unavailable"
            // report, the stored inventory to null — so there is nothing a handler could add.
            // The inventory is the stored report (same fallback chain as /api/diag/summary),
            // not a live query: cost per person is a projection like everything else.
            var rosterTask = signIns.GetAsync(window, ct);
            var stored = await store.LoadAsync(ct);
            var inventory = stored.IsSuccess && stored.Value is not null
                ? stored.Value
                : await ReportFileCache.TryLoadFromFileAsync(env, logger, ct);

            return Results.Ok(SignInReportBuilder.AttachCosts(await rosterTask, inventory));
        })
        .RequireRateLimiting(RateLimits.SignIns)
        .WithName("GetSignIns").WithTags("SignIns");

        return app;
    }
}
