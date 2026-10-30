namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One app's PageSpeed Insights (Lighthouse, mobile) category scores, 0–100. Written nightly by
/// <c>.github/workflows/screenshots.yml</c> into <c>lighthouse.json</c> beside the screenshots.
/// </summary>
public record LighthouseScores
{
    public int Performance { get; init; }
    public int Accessibility { get; init; }
    public int BestPractices { get; init; }
    public int Seo { get; init; }
    /// <summary>Nightly performance scores, oldest first, up to 30 — the card's trend line.</summary>
    public List<int> PerformanceHistory { get; init; } = new();
}
