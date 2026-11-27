using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// What this tier needs and whether it is there, probed once per run: an app answering <c>/health/live</c> at
/// BASE_URL (default http://localhost:8000) with a stored Azure scan, and a browser Playwright can launch. Without either, every test SKIPS
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
            if (new Uri(BaseUrl).IsLoopback)
                http.DefaultRequestHeaders.Add("X-Fake-User", "browser-test");
            try
            {
                if (!http.GetAsync($"{BaseUrl}/health/live").GetAwaiter().GetResult().IsSuccessStatusCode)
                    return NoApp;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return NoApp;
            }

            // Every page here is drawn from a stored Azure scan. A fresh local app has none, and without
            // one the suite used to run for ~55 minutes and fail 21 tests on 40 s waits for cards that
            // can never render. One clear skip instead.
            try
            {
                var summary = http.GetAsync($"{BaseUrl}/api/diag/summary").GetAwaiter().GetResult();
                if (summary.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return "The private Azure pages require Microsoft sign-in. Run this tier against a local Development host.";
                if (summary.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return NoScan;
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

    private static string NoScan =>
        $"The app at {BaseUrl} has no stored Azure scan (/api/diag/summary is 404), so no page has data to draw. "
        + "Copy production's: curl -s https://app-popunkoutersoftware2.azurewebsites.net/api/diag/report -o "
        + "src/PoPunkouterSoftware.Api/App_Data/azure-full-report.json (git-ignored), then rerun.";

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
