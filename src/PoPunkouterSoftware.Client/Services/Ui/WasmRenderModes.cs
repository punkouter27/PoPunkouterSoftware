using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// SOLID: Open/Closed — centralises the render mode decision so adding new pages
/// never requires re-reading framework docs about prerender vs no-prerender.
///
/// This app uses a pure WASM model: the server is an API host only.
/// Prerendering is disabled to avoid injecting WASM-only services
/// (IWebAssemblyHostEnvironment, HttpClient) into the server DI container during SSR.
/// If SEO prerendering is required in future, register stub services in server DI and
/// flip prerender back to true here — one change, all pages update.
/// </summary>
public static class WasmRenderModes
{
    public static IComponentRenderMode Interactive { get; } =
        new InteractiveWebAssemblyRenderMode(prerender: false);
}
