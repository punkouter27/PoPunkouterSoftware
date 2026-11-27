using System.Text.Json;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The brain vault's server side: roles, copies, which repos share a socket, the stars in
/// vault_gold.json (old <c>true</c> entries and the page's note strings), and GitHub links.
/// </summary>
public sealed class GlobalVaultTests
{
    private static GlobalVaultDto VaultFrom(string fleetJson, string? goldJson = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-vault", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), fleetJson);
            if (goldJson is not null)
                File.WriteAllText(Path.Combine(dir, "config", "vault_gold.json"), goldJson);
            return new GlobalDataStore(dir).LoadVault();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string TwoRepos = """
        {
          "generated": "2026-01-01T00:00:00Z",
          "repos": {
            "PoA": {
              "name": "PoA", "kind": "unity", "is_rl": true, "rl_stack": "unity_mujoco",
              "git": { "is_git": true, "remote": "https://github.com/someone/PoA.git" },
              "onnx": [
                { "rel": "PoA/models/walk.onnx", "name": "walk.onnx", "kb": 10.0, "shipped": true, "obs": 103, "act": 29, "sha1": "aaa", "opset": 17,
                  "inputs": [["obs", ["batch", 103]]], "outputs": [["actions", ["batch", 29]], ["value", ["batch", 1]]] },
                { "rel": "PoA/models/walk_critic.onnx", "name": "walk_critic.onnx", "kb": 5.0, "shipped": true, "obs": 103, "act": 1, "sha1": "bbb" },
                { "rel": "PoA/models/value_head.onnx", "name": "value_head.onnx", "kb": 5.0, "shipped": true, "obs": 103, "act": 29, "sha1": "ccc" }
              ],
              "mjcf": [ { "rel": "PoA/bodies/g1.xml", "name": "g1.xml", "model": "g1", "joints": 29, "actuators": 29, "bodies": 30, "free_joints": 1, "timestep": "0.002" } ]
            },
            "PoB": {
              "name": "PoB", "kind": "unreal", "is_rl": true,
              "git": { "is_git": true, "slug": "someone/PoB" },
              "onnx": [
                { "rel": "PoB/brains/walk copy.onnx", "name": "walk copy.onnx", "kb": 10.0, "shipped": false, "obs": 103, "act": 29, "sha1": "aaa" },
                { "rel": "PoB/brains/odd.onnx", "name": "odd.onnx", "kb": 1.0, "shipped": true, "obs": 7, "act": 3, "sha1": "ddd" }
              ]
            },
            "PoC": {
              "name": "PoC", "kind": "blazor",
              "git": { "is_git": true },
              "onnx": [ { "rel": "PoC/x.onnx", "name": "x.onnx", "kb": 1.0, "shipped": true, "sha1": "eee" } ]
            }
          }
        }
        """;

    [Fact]
    public void Roles_FollowTheOldVaultRule()
    {
        var vault = VaultFrom(TwoRepos);

        vault.Brains.Single(b => b.Name == "walk.onnx").Role.Should().Be("policy");
        vault.Brains.Single(b => b.Name == "walk_critic.onnx").Role.Should().Be("critic", "one action is a value head");
        vault.Brains.Single(b => b.Name == "value_head.onnx").Role.Should().Be("critic", "the name says value");
    }

    [Fact]
    public void Copies_CountTheSameContentAcrossTheFleet()
    {
        var vault = VaultFrom(TwoRepos);

        vault.Brains.Single(b => b.Name == "walk.onnx").Copies.Should().Be(2);
        vault.Brains.Single(b => b.Name == "odd.onnx").Copies.Should().Be(1);
    }

    [Fact]
    public void Sockets_ListTheReposThatRunAPolicyOnThem()
    {
        var vault = VaultFrom(TwoRepos);

        var shared = vault.Sockets.Single(s => s.Socket == "103 → 29");
        shared.Repos.Should().Equal("PoA", "PoB");
        shared.Count.Should().Be(3, "the count covers every brain on the socket, critics included");
        vault.Sockets[0].Socket.Should().Be("103 → 29", "sockets shared by more repos come first");
        vault.Sockets.Single(s => s.Socket == "unknown").Repos.Should().BeEmpty();

        vault.Brains.Single(b => b.Name == "walk.onnx").Fits.Should().Equal("PoB");
        vault.Brains.Single(b => b.Name == "odd.onnx").Fits.Should().BeEmpty();
    }

    [Fact]
    public void Brains_CarryStackOpsetIoAndAGitHubLink()
    {
        var vault = VaultFrom(TwoRepos);

        var walk = vault.Brains.Single(b => b.Name == "walk.onnx");
        walk.Stack.Should().Be("unity_mujoco");
        walk.Opset.Should().Be(17);
        walk.Sha1.Should().Be("aaa");
        walk.Io.Should().Be("obs [batch, 103] → actions [batch, 29], value [batch, 1]");
        walk.Url.Should().Be("https://github.com/someone/PoA/blob/HEAD/models/walk.onnx");

        vault.Brains.Single(b => b.Name == "walk copy.onnx").Url.Should().Be("https://github.com/someone/PoB/blob/HEAD/brains/walk%20copy.onnx");
        vault.Brains.Single(b => b.Name == "x.onnx").Url.Should().BeNull("PoC has no remote");
        vault.Models.Single().Url.Should().Be("https://github.com/someone/PoA/blob/HEAD/bodies/g1.xml");
        vault.Models.Single().FreeJoints.Should().Be(1);
    }

    [Fact]
    public void Gold_AcceptsTheOldTrueAndThePagesNotes_AndIgnoresFalse()
    {
        var vault = VaultFrom(TwoRepos, """
            {"gold":{"PoA/models/walk.onnx":"starred 2026-11-27","PoB/brains/odd.onnx":true,"PoC/x.onnx":false,"PoA/models/value_head.onnx":null}}
            """);

        vault.GoldCount.Should().Be(2);
        vault.Brains.Single(b => b.Name == "walk.onnx").GoldNote.Should().Be("starred 2026-11-27");
        vault.Brains.Single(b => b.Name == "odd.onnx").Gold.Should().BeTrue();
        vault.Brains.Single(b => b.Name == "x.onnx").Gold.Should().BeFalse();
        vault.Brains.Single(b => b.Name == "value_head.onnx").Gold.Should().BeFalse();
    }

    [Fact]
    public void QueuePlan_RoundTripsThroughTheTrimmedContext()
    {
        var plan = new GlobalQueuePlanDto
        {
            Name = "tonight",
            CoolTemp = 65,
            GapMin = 0,
            RefreshAfter = false,
            Jobs = [new LocalHelperQueueItem { Id = "a", Hours = 2.5 }, new LocalHelperQueueItem { Id = "b", Hours = 1 }],
        };

        var json = JsonSerializer.Serialize(plan, GlobalJsonContext.Default.GlobalQueuePlanDto);
        var back = JsonSerializer.Deserialize(json, GlobalJsonContext.Default.GlobalQueuePlanDto)!;

        back.Jobs.Select(j => (j.Id, j.Hours)).Should().Equal(("a", 2.5), ("b", 1.0));
        back.CoolTemp.Should().Be(65);
        back.RefreshAfter.Should().BeFalse();
        json.Should().NotContain("command");
    }

    [Fact]
    public void LocalGold_KeepsUnstarsAsNull()
    {
        var local = new GlobalVaultGoldLocalDto { Gold = { ["PoA/a.onnx"] = "starred", ["PoB/b.onnx"] = null } };

        var back = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(local, GlobalJsonContext.Default.GlobalVaultGoldLocalDto),
            GlobalJsonContext.Default.GlobalVaultGoldLocalDto)!;

        back.Gold.Should().ContainKey("PoB/b.onnx").WhoseValue.Should().BeNull();
        back.Gold["PoA/a.onnx"].Should().Be("starred");
    }

    [Fact]
    public void CommittedSnapshot_VaultHasPoliciesCriticsAndLinks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoPunkouterSoftware.slnx")))
            dir = dir.Parent;
        var vault = new GlobalDataStore(Path.Combine(dir!.FullName, "src", "PoPunkouterSoftware.Api", "App_Data", "global")).LoadVault();

        vault.Brains.Should().Contain(b => b.Role == "policy").And.Contain(b => b.Role == "critic");
        vault.Brains.Where(b => b.Url is not null).Should().OnlyContain(b => b.Url!.StartsWith("https://github.com/", StringComparison.Ordinal));
        vault.Brains.Should().OnlyContain(b => b.Copies >= 1);
    }
}
