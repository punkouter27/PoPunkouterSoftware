namespace PoPunkouterSoftware.Shared;

/// <summary>
/// The ship board's stage rules, shared by the API (which suggests a stage from the crawl) and
/// the /global/readiness page (which re-suggests as soon as a checklist box is ticked, before
/// anything is saved). One copy, so the two can never disagree.
/// </summary>
public static class GlobalReadinessRules
{
    /// <summary>The five stages, in board order.</summary>
    public static IReadOnlyList<string> Stages { get; } = ["prototype", "playable", "polishing", "ready", "shipped"];

    public static string Label(string? stage) => stage switch
    {
        "prototype" => "Prototype",
        "playable" => "Playable",
        "polishing" => "Polishing",
        "ready" => "Ready to ship",
        "shipped" => "Shipped",
        _ => stage ?? "",
    };

    public static bool IsStage(string? stage) => stage is not null && Stages.Contains(stage);

    /// <summary>
    /// The suggested stage and why, from what the board can see (GLOBAL's readiness_state):
    /// the ticked checklist items first, then a live URL with green CI, then captures.
    /// </summary>
    public static (string Stage, string Why) Suggest(int done, int total, string? liveUrl, string? ci, int mediaCount)
    {
        var live = !string.IsNullOrEmpty(liveUrl);
        if (total > 0 && done == total)
            return ("ready", $"all {total} checklist items ticked");
        if (done >= 4)
            return ("polishing", $"{done}/{total} checklist items ticked");
        if (live && string.Equals(ci, "success", StringComparison.OrdinalIgnoreCase))
            return ("playable", "deployed (live URL in README) and CI is green");
        if (live)
            return ("playable", "has a live URL");
        if (mediaCount >= 5)
            return ("playable", "has a capture reel");
        return ("prototype", "no live URL and few captures");
    }
}
