using Microsoft.JSInterop;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// The refresh lifecycle's sound cues (drone, boom, outcome motif), played through
/// js/audio-kit.js behind the visitor's one sound opt-in.
///
/// <para>The "Listen" button that played the whole ops summary as a four-second chord
/// (<c>audioKit.sonifyOps</c>) was removed 2026-11-07: a second switch for sound beside
/// /settings, hidden behind Details → Full, restating numbers the hero already prints.</para>
/// </summary>
public partial class AzureDashboard
{
    /// <summary>
    /// Fire-and-forget SFX. Every call site is inside the refresh lifecycle, where an audio
    /// failure must never change what the scan does or how its outcome is reported — so this
    /// swallows everything and returns nothing.
    /// </summary>
    private async Task SfxAsync(string identifier, params object?[] args)
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
