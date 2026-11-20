using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// What this tier needs and whether it is there, probed once per run: an app answering <c>/health/live</c> at
/// BASE_URL (default http://localhost:8000) and a browser Playwright can launch. Without either, every test SKIPS
/// with one clear reason instead of failing on "connection refused" or "Executable doesn't exist".
/// </summary>
public static class LiveApp
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000").TrimEnd('/');

    private static readonly Lazy<string?> Probe = new(() =>
    {
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(BaseUrl.Contains("localhost") ? 5 : 90) })
        {
            try
            {
                if (!http.GetAsync($"{BaseUrl}/health/live").GetAwaiter().GetResult().IsSuccessStatusCode)
                    return NoApp;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return NoApp;
            }
        }

        // BROWSER_CHANNEL=chrome drives the installed Google Chrome; otherwise the bundled Chromium must exist.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BROWSER_CHANNEL")))
            return null;
        using var pw = Playwright.CreateAsync().GetAwaiter().GetResult();
        return File.Exists(pw.Chromium.ExecutablePath)
            ? null
            : "Playwright's Chromium is not installed. Run once: pwsh tests/PoPunkouterSoftware.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium";
    });

    private static string NoApp =>
        $"No app is answering at {BaseUrl}/health/live. Start it with ./scripts/run-dev.ps1 (or set BASE_URL "
        + "to a running instance), then rerun. E2E tiers are local-only and never run in CI.";

    /// <summary>Null when the tier can run; otherwise why it cannot.</summary>
    public static string? SkipReason => Probe.Value;
}

/// <summary>A <see cref="FactAttribute"/> that skips when no app or no browser is available.</summary>
public sealed class LiveAppFactAttribute : FactAttribute
{
    public LiveAppFactAttribute() => Skip = LiveApp.SkipReason;
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when no app or no browser is available.</summary>
public sealed class LiveAppTheoryAttribute : TheoryAttribute
{
    public LiveAppTheoryAttribute() => Skip = LiveApp.SkipReason;
}
