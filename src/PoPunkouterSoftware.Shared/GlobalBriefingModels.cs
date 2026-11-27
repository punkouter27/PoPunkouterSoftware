namespace PoPunkouterSoftware.Shared;

/// <summary>One new or changed <c>.onnx</c> brain in the briefing period.</summary>
public sealed class GlobalBriefingBrainDto
{
    public string Name { get; set; } = "";

    /// <summary>Workspace-relative path (<c>PoBox/Assets/…/match.onnx</c>).</summary>
    public string Rel { get; set; } = "";
}

/// <summary>Where a harvested lesson was found: one line in one repo's docs.</summary>
public sealed class GlobalLessonSourceDto
{
    public string Repo { get; set; } = "";

    /// <summary>Workspace-relative path (<c>PoBox/AGENTS.md</c>).</summary>
    public string? File { get; set; }
    public int Line { get; set; }
}
