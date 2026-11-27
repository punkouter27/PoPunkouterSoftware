using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The ship board's stage rules (shared by the API and the page) and the tri-state checklist
/// readiness.json holds: yes, no, or not answered.
/// </summary>
public sealed class GlobalReadinessTests
{
    [Theory]
    [InlineData(6, null, null, 0, "ready")]
    [InlineData(4, null, null, 0, "polishing")]
    [InlineData(5, "https://x", "success", 9, "polishing")]
    [InlineData(1, "https://x", "success", 0, "playable")]
    [InlineData(0, "https://x", "failure", 0, "playable")]
    [InlineData(0, null, "success", 5, "playable")]
    [InlineData(3, null, "success", 4, "prototype")]
    public void Suggest_FollowsTheStationOrder(int done, string? live, string? ci, int media, string expected)
    {
        GlobalReadinessRules.Suggest(done, 6, live, ci, media).Stage.Should().Be(expected);
    }

    [Fact]
    public void Suggest_SaysWhy()
    {
        GlobalReadinessRules.Suggest(6, 6, null, null, 0).Why.Should().Be("all 6 checklist items ticked");
        GlobalReadinessRules.Suggest(4, 6, null, null, 0).Why.Should().Be("4/6 checklist items ticked");
        GlobalReadinessRules.Suggest(0, 6, "https://x", "success", 0).Why.Should().Contain("CI is green");
        GlobalReadinessRules.Suggest(0, 6, null, null, 0).Why.Should().Be("no live URL and few captures");
    }

    [Fact]
    public void Stages_AreTheFiveBoardColumnsInOrder()
    {
        GlobalReadinessRules.Stages.Should().Equal("prototype", "playable", "polishing", "ready", "shipped");
        GlobalReadinessRules.Label("ready").Should().Be("Ready to ship");
        GlobalReadinessRules.IsStage("launched").Should().BeFalse();
        GlobalReadinessRules.IsStage(null).Should().BeFalse();
    }

    [Fact]
    public void LoadReadiness_KeepsYesNoAndUnanswered_AndAStageSetInConfigWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-readiness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), """
                {"generated":"2026-01-02T00:00:00Z","repos":{
                  "PoDemo":{"name":"PoDemo","kind":"unity","is_rl":true,"git":{"is_git":true},"docs":{"live_url":"https://demo"},
                            "ci":{"latest_run":{"conclusion":"success"}},"media":{"count":7}},
                  "PoTool":{"name":"PoTool","kind":"blazor","git":{"is_git":true},"media":{"count":1}}}}
                """);
            File.WriteAllText(Path.Combine(dir, "config", "readiness.json"), """
                {"apps":{"PoDemo":{"stage":"polishing","notes":"needs audio","checklist":{"core_loop":true,"perf":false}}}}
                """);

            var apps = new GlobalDataStore(dir).LoadReadiness().Apps.ToDictionary(a => a.Name);

            var demo = apps["PoDemo"];
            demo.Stage.Should().Be("polishing");
            demo.StageIsSet.Should().BeTrue();
            demo.Suggested.Should().Be("playable", "a live URL with green CI, and only 1 of 6 ticked");
            demo.Notes.Should().Be("needs audio");
            demo.MediaCount.Should().Be(7);
            demo.Done.Should().Be(1);
            demo.Checklist.Single(c => c.Key == "core_loop").Value.Should().BeTrue();
            demo.Checklist.Single(c => c.Key == "perf").Value.Should().BeFalse();
            demo.Checklist.Single(c => c.Key == "perf").Done.Should().BeFalse();
            demo.Checklist.Single(c => c.Key == "audio_fx").Value.Should().BeNull();

            var tool = apps["PoTool"];
            tool.StageIsSet.Should().BeFalse();
            tool.Stage.Should().Be("prototype");
            tool.Checklist.Should().OnlyContain(c => c.Value == null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
