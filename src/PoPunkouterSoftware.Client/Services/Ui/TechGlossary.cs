namespace PoPunkouterSoftware.Client;

/// <summary>
/// The home page matrix's hints. A column or Tech stack line gets a sentence saying what it
/// holds (<see cref="Sections"/>). A technology's hint says ONLY how that one app uses it — its
/// <c>stack.details</c> note in apps.json — never what the technology is in general (owner's
/// call, 2026-11-19: the generic "what it is" glossary of ~100 terms was deleted).
/// </summary>
public static class TechGlossary
{
    /// <summary>What each column and each Tech stack line means.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["app"] = "The app's name, a picture of it and what it does. The name opens the live app in a new tab.",
        ["signIn"] = "How a visitor signs in to the app, or None when it has no login.",
        ["techstack"] = "What the app is built from: what runs in the browser, what runs on the server, how it pushes live updates and where it keeps its data. For a game: the engine, the physics and how its AI was trained.",
        ["ai"] = "Every AI model the app uses, grouped by where it runs: Azure, Google, or inside the app itself.",
        ["delivery"] = "How new code gets from GitHub to the live site.",
        ["extras"] = "Libraries and services that only this app uses.",
        ["shared"] = "These are the same for every app in this table, so they are listed once here instead of filling a column.",
        ["frontend"] = "What runs in the visitor's browser and draws the page.",
        ["engine"] = "The game engine: what draws the game and runs its logic.",
        ["backend"] = "What runs on the server: the API the page calls.",
        ["realtime"] = "How the server pushes updates to the page the moment they happen, without the page asking again.",
        ["data"] = "Where the app keeps its data.",
        ["physics"] = "What simulates the bodies, so they move, collide and fall like real objects.",
        ["training"] = "How the game's AI players learned, by reinforcement learning: thousands of practice matches, rewarded for winning.",
    };

    /// <summary>A value's hint: how THIS app uses it (its <c>stack.details</c> note or an AI
    /// service's <c>use</c>), or null when there is no note or it only repeats the label.</summary>
    public static string? Hint(string value, string? note)
    {
        if (string.IsNullOrWhiteSpace(note) || note == value) return null;
        note = note.Trim();
        return ".!?)".Contains(note[^1]) ? note : note + ".";
    }
}
