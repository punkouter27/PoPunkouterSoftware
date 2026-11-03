namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One AI model or service an app calls — a line in the home page matrix's AI column.
/// Curated in <c>apps.json</c> from each repo's source (2026-11-03); Azure cannot report it,
/// because every app shares the one AI account in PoShared.
/// </summary>
public record AiService
{
    /// <summary>"Azure", "Google", or "In-app" (runs inside the app or the browser, no paid service).</summary>
    public string Cloud { get; init; } = "";
    /// <summary>The model or service as a visitor would name it, e.g. "GPT-5 mini".</summary>
    public string Name { get; init; } = "";
    /// <summary>What the app uses it for, one short sentence — the hint.</summary>
    public string Use { get; init; } = "";
    /// <summary>Wired in the code but off unless configured, so not part of a default run.</summary>
    public bool Optional { get; init; }
}
