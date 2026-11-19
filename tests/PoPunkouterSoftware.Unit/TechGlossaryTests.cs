using PoPunkouterSoftware.Client;

namespace PoPunkouterSoftware.Unit;

/// <summary>The home page hints: only how one app uses a technology, never what it is.</summary>
public class TechGlossaryTests
{
    [Fact]
    public void Hint_IsOnlyTheAppsOwnNote()
    {
        TechGlossary.Hint("SignalR", "pushes scores live").Should().Be("pushes scores live.");
        TechGlossary.Hint("SignalR", " ends with a period. ").Should().Be("ends with a period.");

        // No note, or a note that only repeats the label: no hint, not a generic definition.
        TechGlossary.Hint("SignalR", null).Should().BeNull();
        TechGlossary.Hint("SignalR", "SignalR").Should().BeNull();
    }
}
