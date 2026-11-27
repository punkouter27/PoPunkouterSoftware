using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// The read contract behind the <c>/global</c> pages: the fleet, brains, lessons, ship board,
/// briefing, RL facts and the UNITE catalogue that used to be generated static HTML in GLOBAL.
/// </summary>
/// <remarks>
/// Every route requires Microsoft sign-in (owner decision, 2026-11-27). The snapshot describes
/// the owner's machine — local paths, uncommitted files, commit hashes — so it is private by
/// default, and the browser asks for it only on a signed-in page.
/// </remarks>
internal static class GlobalEndpoints
{
    internal static WebApplication MapGlobalEndpoints(this WebApplication app)
    {
        var global = app.MapGroup("/api/global").WithTags("Global").RequireAuthorization();

        global.MapGet("/summary", (GlobalDataStore store) => Results.Ok(store.LoadSummary()))
            .WithName("GetGlobalSummary");

        global.MapGet("/home", (GlobalDataStore store) => Results.Ok(store.LoadHome()))
            .WithName("GetGlobalHome");

        global.MapGet("/fleet", (GlobalDataStore store) => Results.Ok(store.LoadFleetView()))
            .WithName("GetGlobalFleet");

        global.MapGet("/vault", (GlobalDataStore store) => Results.Ok(store.LoadVault()))
            .WithName("GetGlobalVault");

        global.MapGet("/lessons", (GlobalDataStore store) =>
            {
                var lessons = store.LoadLessons();
                return Results.Ok(new GlobalLessonsFileDto { Count = lessons.Count, Lessons = lessons });
            })
            .WithName("GetGlobalLessons");

        global.MapGet("/readiness", (GlobalDataStore store) => Results.Ok(store.LoadReadiness()))
            .WithName("GetGlobalReadiness");

        global.MapGet("/briefing", (GlobalDataStore store) => Results.Ok(store.LoadBriefing()))
            .WithName("GetGlobalBriefing");

        global.MapGet("/rl", (GlobalDataStore store) => Results.Ok(store.LoadRl()))
            .WithName("GetGlobalRl");

        global.MapGet("/queue", (GlobalDataStore store) => Results.Ok(store.LoadQueue()))
            .WithName("GetGlobalQueue");

        global.MapGet("/standards", (GlobalDataStore store) => Results.Ok(store.LoadStandards()))
            .WithName("GetGlobalStandards");

        global.MapGet("/unite", (GlobalDataStore store) => Results.Ok(store.LoadUnite()))
            .WithName("GetGlobalUnite");

        return app;
    }
}
