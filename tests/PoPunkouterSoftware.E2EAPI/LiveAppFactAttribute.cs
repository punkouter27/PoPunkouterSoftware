namespace PoPunkouterSoftware.E2EAPI;

/// <summary>
/// The app this tier talks to (BASE_URL, default http://localhost:8000) and whether it answers
/// <c>/health/live</c>, probed once per run. Without a running app every test SKIPS with one clear reason
/// instead of failing 21 times with "connection refused".
/// </summary>
public static class LiveApp
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000").TrimEnd('/');

    public static string SkipReason =>
        $"No app is answering at {BaseUrl}/health/live. Start it with ./scripts/run-dev.ps1 (or set BASE_URL "
        + "to a running instance), then rerun. E2E tiers are local-only and never run in CI.";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        // A cold App Service (when BASE_URL is production) can take tens of seconds to answer.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(BaseUrl.Contains("localhost") ? 5 : 90) };
        try
        {
            return http.GetAsync($"{BaseUrl}/health/live").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    });

    public static bool IsUp => Probe.Value;
}

/// <summary>A <see cref="FactAttribute"/> that skips when no app is running at BASE_URL.</summary>
public sealed class LiveAppFactAttribute : FactAttribute
{
    public LiveAppFactAttribute()
    {
        if (!LiveApp.IsUp)
            Skip = LiveApp.SkipReason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when no app is running at BASE_URL.</summary>
public sealed class LiveAppTheoryAttribute : TheoryAttribute
{
    public LiveAppTheoryAttribute()
    {
        if (!LiveApp.IsUp)
            Skip = LiveApp.SkipReason;
    }
}
