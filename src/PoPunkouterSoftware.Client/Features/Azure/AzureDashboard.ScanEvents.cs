using Microsoft.JSInterop;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Reports the refresh lifecycle to the page (js/helpers.js <c>appScan</c>, which dispatches the
/// <c>app:refresh</c> event the GPU backdrop draws its sweep and shockwave from).
///
/// <para>Until 2026-11-17 these calls went through js/audio-kit.js, which also played a drone
/// and an outcome chord for visitors who turned sound on. Sound is now permanently off
/// (owner's call), so the audio kit was deleted and only the event remains.</para>
/// </summary>
public partial class AzureDashboard
{
    /// <summary>
    /// Fire-and-forget. Every call site is inside the refresh lifecycle, where a decoration
    /// failure must never change what the scan does or how its outcome is reported — so this
    /// swallows everything and returns nothing.
    /// </summary>
    private async Task ScanEventAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
        catch (TaskCanceledException) { }
    }
}
