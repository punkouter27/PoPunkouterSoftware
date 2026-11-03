using PoPunkouterSoftware.Client;

namespace PoPunkouterSoftware.Unit;

/// <summary>The home page hints: what a term is, then how one app uses it.</summary>
public class TechGlossaryTests
{
    [Fact]
    public void Hint_DescribesVersionedAndCombinedTerms_ThenAddsTheAppsNote()
    {
        // A version falls back to the longest whole-word prefix, never a different word.
        TechGlossary.Describe("MuJoCo 3.10").Should().StartWith("MuJoCo, a physics engine");
        TechGlossary.Describe("Unity ML-Agents 4.1").Should().StartWith("Unity's toolkit for training");
        TechGlossary.Describe("UniTask").Should().StartWith("Fast, allocation-free");

        // Several pieces in one label: each named, so the sentences do not run together.
        TechGlossary.Describe("SignalR + WebSockets").Should()
            .StartWith("SignalR: ").And.Contain(" WebSockets: ");

        // The app's note follows, as a sentence; a term nobody described still gets its note.
        TechGlossary.Hint("SignalR", "pushes scores live", "PoFightJudge").Should()
            .EndWith(" In PoFightJudge: pushes scores live.");
        TechGlossary.Hint("Something new", "a note", "PoX").Should().Be("In PoX: a note.");
        TechGlossary.Hint("Something new", null, "PoX").Should().BeNull();
    }
}
