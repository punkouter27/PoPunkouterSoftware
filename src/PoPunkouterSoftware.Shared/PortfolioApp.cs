namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One stable home-page portfolio row. The catalog in apps.json is authoritative and
/// live Azure inventory decorates it with current reachability.
///
/// <para>Deliberately narrow: this is exactly what <c>PortfolioStackTable</c> binds, plus
/// <see cref="Status"/>, which the API contract tests pin, and <see cref="Category"/>.
/// <c>InventoryGeneratedAt</c> and <c>Technologies</c> were serialised onto every card in
/// the response and read by nothing after the design pass stripped the card down to
/// screenshot + name + description + link. They stay in apps.json as catalog metadata.
/// <see cref="GithubRepo"/> came back on 2026-10-30 with a reader: the Ctrl+K palette.</para>
/// </summary>
public record PortfolioApp
{
    /// <summary>"rl-training" puts the card in the home page's RL training section (the
    /// Unity games on Static Web Apps, 2026-10-27); anything else is a web app.</summary>
    public const string RlTraining = "rl-training";

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string Status { get; init; } = "unknown";
    /// <summary>API path of the stored home-page screenshot; null when none captured yet.</summary>
    public string? ScreenshotUrl { get; init; }
    /// <summary>Latest nightly PageSpeed Insights scores; null until one has been captured.</summary>
    public LighthouseScores? Scores { get; init; }
    /// <summary>
    /// Observed days since this app's last down or degraded day on the 30-day uptime grid; 30
    /// means every day of the window was seen and clean. Null when the app was never observed — no streak to claim.
    /// </summary>
    public int? CleanDays { get; init; }
    /// <summary>"owner/repo" from the catalog — the Ctrl+K palette's repo and Actions links. Null when the catalog names none.</summary>
    public string? GithubRepo { get; init; }
    /// <summary>github.com link to the app's source; null when the catalog names no repo or the
    /// scan found it private (an anonymous visitor would get a 404).</summary>
    public string? SourceUrl { get; init; }
    /// <summary>Google Play listing; set only once the scan has seen the public page answer.</summary>
    public string? PlayStoreUrl { get; init; }
    /// <summary>The home page matrix's row: what the app is built from.</summary>
    public AppStack Stack { get; init; } = new();
}
