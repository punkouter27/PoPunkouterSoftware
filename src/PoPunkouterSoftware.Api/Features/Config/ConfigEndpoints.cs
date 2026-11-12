namespace PoPunkouterSoftware.Api;

/// <summary>
/// Lets the client discover the canonical API base URL, environment mode, and feature
/// availability. One endpoint, one consumer: <c>AzureDashboard</c> reads it to decide
/// whether to render the management controls.
/// <para><c>/api/whoami</c>, <c>/api/logout</c> and <c>/api/impersonate</c> used to live
/// here. They were affordances of the Session/Logout header slot, which was deleted in
/// 2026-08 because a statically-rendered <c>MainLayout</c> can never fire an
/// <c>@onclick</c>. Nothing has called them since, so they went the same way as the three
/// slices deleted in 2026-07 — see the "every endpoint needs a consumer" rule in
/// CLAUDE.md. <see cref="FakeAuthHandler"/> itself stays: it is what lets a caller opt
/// into the management role via <c>X-Fake-Roles</c>.</para>
/// </summary>
internal static class ConfigEndpoints
{
    internal static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        // Exactly the three fields AppJsonContext.ConfigResponse deserialises — nothing
        // more. Every extra field previously returned here (isProduction, AI flags, a
        // hardcoded model catalogue) was discarded on arrival by the client.
        //
        // isMockMode=true tells the UI to display the "MOCK DATA" banner (rule 10).
        // Activated when ASPNETCORE_ENVIRONMENT is "Testing" (integration / E2E test runs).
        // NOTE: no connection strings or keys are echoed here — the browser needs to know
        // which capabilities are on, not whether a secret is present. That stays server-side.
        app.MapGet("/api/config",
            (HttpContext ctx, IWebHostEnvironment env, IConfiguration config) =>
                Results.Ok(new
                {
                    apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
                    isMockMode = env.IsEnvironment("Test") || env.IsEnvironment("Testing"),
                    // "Can THIS browser use them", not "is the flag on": Production always demands
                    // the management key, which the browser never holds, so there the Sync/Rescan
                    // buttons could only answer 401. The flag stays on in Production for the
                    // nightly uptime-scan workflow, which does hold the key.
                    managementActionsEnabled =
                        config.GetValue<bool>("FeatureFlags:EnableManagementActions", env.IsDevelopment() || env.IsEnvironment("Test") || env.IsEnvironment("Testing"))
                        && !env.IsProduction(),
                }))
            .WithName("GetConfig").WithTags("Config");

        return app;
    }
}
