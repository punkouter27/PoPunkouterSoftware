using System.Text.Json;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The GLOBAL snapshot the crawler commits. These tests run against the real
/// <c>src/PoPunkouterSoftware.Api/App_Data/global</c> files: the point of the migration is that the
/// app can read what the crawler writes, so a drift between the two must fail the build rather
/// than show an empty page in production.
/// </summary>
public sealed class GlobalDataStoreTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoPunkouterSoftware.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found above " + AppContext.BaseDirectory);
    }

    private static string SnapshotDir() =>
        Path.Combine(RepoRoot(), "src", "PoPunkouterSoftware.Api", "App_Data", "global");

    private static GlobalDataStore Store() => new(SnapshotDir());

    [Fact]
    public void CommittedSnapshot_IsFound()
    {
        Directory.Exists(SnapshotDir()).Should().BeTrue(
            "the crawl is committed with the repo; without it every /global page is empty");
        File.Exists(Path.Combine(SnapshotDir(), "fleet.json")).Should().BeTrue();
    }

    [Fact]
    public void TimelineLines_Parse()
    {
        var path = Path.Combine(SnapshotDir(), "history", "station_timeline.jsonl");
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        lines.Should().NotBeEmpty();

        foreach (var line in lines)
        {
            var entry = JsonSerializer.Deserialize(line, GlobalCrawlerJsonContext.Default.GlobalTimelineEntryDto);
            entry.Should().NotBeNull();
            entry!.Repos.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void RlFacts_Parse()
    {
        var path = Path.Combine(SnapshotDir(), "rl_facts.json");
        using var stream = File.OpenRead(path);
        var facts = JsonSerializer.Deserialize(stream,
            GlobalCrawlerJsonContext.Default.DictionaryStringGlobalRlRepoFactDto);

        facts.Should().NotBeNull();
        facts!.Should().HaveCount(10);
    }

    [Fact]
    public void Fleet_ReadsEveryRepoWithItsNameAndGitState()
    {
        var fleet = Store().LoadFleetView();

        fleet.Repos.Should().NotBeEmpty();
        fleet.Repos.Should().OnlyContain(r => r.Name.Length > 0);
        fleet.Repos.Select(r => r.Name).Should().Contain(["PoBasketball", "PoBox", "PoPunkouterSoftware"]);

        var poBox = fleet.Repos.Single(r => r.Name == "PoBox");
        poBox.Git.IsGit.Should().BeTrue();
        poBox.Kind.Should().Be("unity");
        poBox.Onnx.Should().NotBeEmpty();
        poBox.Disk.TotalBytes.Should().BePositive();
        poBox.Git.Commits30d.Should().BePositive("commits_30d is a snake_case key with a digit in it");
        poBox.Git.Commits7d.Should().BePositive("commits_7d is a snake_case key with a digit in it");
    }

    [Fact]
    public void Fleet_NormalisesSectionsTheCrawlerWritesAsNull()
    {
        // A folder that is not a repo has "ci": null, "docs": null, "media": null and
        // "recent_commits": null in fleet.json. Every page reads those sections.
        var fleet = Store().LoadFleetView();

        fleet.Repos.Should().OnlyContain(r => r.Git != null && r.Ci != null
            && r.Docs != null && r.Media != null && r.Disk != null);
        fleet.Repos.Should().OnlyContain(r => r.Git.RecentCommits != null
            && r.Ci.Workflows != null && r.Docs.Tests != null
            && r.Media.Showcase != null && r.Disk.HeavyDirs != null);
    }

    [Fact]
    public void Summary_CountsTheWholeFleet()
    {
        var summary = Store().LoadSummary();

        summary.RepoCount.Should().BeGreaterThan(10);
        summary.RlCount.Should().Be(10);
        summary.BrainCount.Should().BeGreaterThan(summary.ShippedBrainCount);
        summary.LessonsCount.Should().BeGreaterThan(0);
        summary.CreaturesCount.Should().Be(6);
        summary.DiskBytes.Should().BePositive();
    }

    [Fact]
    public void Vault_GroupsBrainsBySocket()
    {
        var vault = Store().LoadVault();

        vault.BrainCount.Should().BeGreaterThan(0);
        vault.Sockets.Should().NotBeEmpty();
        vault.Sockets.Sum(s => s.Count).Should().Be(vault.BrainCount);
        vault.Brains.Should().OnlyContain(b => b.Socket.Length > 0);
        vault.Models.Should().NotBeEmpty();
    }

    [Fact]
    public void Readiness_SuggestsAStageForEveryGitRepo()
    {
        var readiness = Store().LoadReadiness();

        readiness.Apps.Should().NotBeEmpty();
        readiness.Apps.Should().OnlyContain(a => a.Stage.Length > 0 && a.Effective.Length > 0);
        readiness.Apps.Should().OnlyContain(a => a.Checklist.Count == 6);
    }

    [Fact]
    public void Briefing_ComparesTheLastTwoCrawls()
    {
        var briefing = Store().LoadBriefing();

        briefing.IsBaseline.Should().BeFalse("the committed timeline holds more than one crawl");
        briefing.Since.Should().NotBeNull();
        briefing.Generated.Should().BeAfter(briefing.Since!.Value);
        briefing.Items.Should().NotBeEmpty();
    }

    [Fact]
    public void Rl_ReadsFactsAndHistory()
    {
        var rl = Store().LoadRl();

        rl.Repos.Should().HaveCount(10);
        rl.History.Should().NotBeEmpty();
        rl.Repos.Should().OnlyContain(r => r.Found > 0);
        rl.ShippedBrains.Should().BePositive();
    }

    [Fact]
    public void Unite_CataloguesEveryCreature()
    {
        var unite = Store().LoadUnite();

        unite.Creatures.Should().HaveCount(6);
        unite.Creatures.Should().OnlyContain(c => c.Obs == 131 && c.Action == 28);
        unite.Creatures.Should().ContainSingle(c => c.Id == "unitree_r1_a5" && c.Hardware);
        unite.Rules.Markdown.Should().NotBeEmpty();
        unite.Documents.Should().NotBeEmpty();
    }

    [Fact]
    public void Queue_ReadsTheCommittedJobsAndTheirMissingPaths()
    {
        var queue = Store().LoadQueue();

        queue.Generated.Should().NotBeNull();
        queue.Jobs.Should().NotBeEmpty();
        queue.Jobs.Select(j => j.Id).Should().OnlyHaveUniqueItems();
        queue.Jobs.Should().OnlyContain(j => j.Missing != null && j.Missing.All(m => j.Requires.Contains(m)));
    }

    [Fact]
    public void Queue_CarriesTheBehaviorsMostUrgentFirst()
    {
        var queue = Store().LoadQueue();

        queue.Behaviors.Should().NotBeEmpty();
        queue.Behaviors.Should().BeInDescendingOrder(b => b.Urgency);
        queue.Behaviors.Should().OnlyContain(b => b.Urgency == (int)Math.Round(b.Importance * (100 - b.Quality) / 10.0, MidpointRounding.ToEven));
    }

    [Fact]
    public void Home_FlagsTinyAndMissingBenchmarks()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var repos = new[]
        {
            new GlobalRlRepoViewDto { Name = "Big", Benchmark = new() { NumEnvs = 4096, EnvStepsPerS = 40000, When = now.AddDays(-1) } },
            new GlobalRlRepoViewDto { Name = "Tiny", Benchmark = new() { NumEnvs = 4, EnvStepsPerS = 9, When = now.AddDays(-1) } },
            new GlobalRlRepoViewDto { Name = "Old", Benchmark = new() { NumEnvs = 4096, EnvStepsPerS = 40000, When = now.AddDays(-9) } },
            new GlobalRlRepoViewDto { Name = "None" },
        };

        var flags = GlobalDataStore.RlFlags(repos, now);

        flags.Select(f => f.Repo).Should().Equal("None", "Old", "Tiny");
        flags.Single(f => f.Repo == "Tiny").Why.Should().Contain("only 4 envs").And.Contain("9 steps/s is very low");
        flags.Single(f => f.Repo == "Old").Why.Should().Contain("9 days old");
        flags.Single(f => f.Repo == "None").Why.Should().Contain("not zero");
    }

    [Fact]
    public void Home_ComputesTheTrendFromTheTimeline()
    {
        var home = Store().LoadHome();

        home.Trend.Should().HaveCountGreaterThan(1);
        home.Previous.Should().NotBeNull();
        home.Trend.Select(p => p.When).Should().BeInAscendingOrder();
        home.JobsTotal.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Queue_CarriesTheCloudJobs()
    {
        var queue = Store().LoadQueue();

        queue.CloudJobs.Should().NotBeEmpty();
        queue.CloudJobs.Select(j => j.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Standards_ReadTheCommittedDocuments()
    {
        var standards = Store().LoadStandards();

        standards.Documents.Should().NotBeEmpty();
        standards.Documents.Should().OnlyContain(d => d.Path.StartsWith("docs/global-standards/", StringComparison.Ordinal));
        standards.Documents.Select(d => d.Group).Distinct().Should().BeEquivalentTo(["Blazor apps", "RL training"]);
    }

    [Fact]
    public void LocalHelperRequest_SerializesAsTheHelperExpects()
    {
        var json = JsonSerializer.Serialize(new LocalHelperRequest
        {
            Action = "launch_queue",
            Dry = true,
            CoolTemp = 65,
            RefreshAfter = false,
            Jobs = [new LocalHelperQueueItem { Id = "a", Hours = 2.5 }],
        }, GlobalJsonContext.Default.LocalHelperRequest);

        json.Should().Be("""{"action":"launch_queue","dry":true,"coolTemp":65,"refreshAfter":false,"jobs":[{"id":"a","hours":2.5}]}""");
    }

    [Fact]
    public void LocalHelperResult_ReadsTheHelperReply()
    {
        var reply = JsonSerializer.Deserialize("""{"ok": true, "output": "Started", "job": "refresh_1", "authorized": true, "ids": ["x"]}""",
            GlobalJsonContext.Default.LocalHelperResult);

        reply!.Ok.Should().BeTrue();
        reply.Authorized.Should().BeTrue();
        reply.Job.Should().Be("refresh_1");
        reply.Done.Should().BeNull();
    }

    [Fact]
    public void Queue_WithoutACrawl_FallsBackToTheJobListUnchecked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-queue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config", "train_jobs.json"),
                """{"jobs":[{"id":"a","repo":"PoBox","command":"train --hours {hours}","default_hours":6,"self_limiting":true}]}""");

            var queue = new GlobalDataStore(dir).LoadQueue();

            var job = queue.Jobs.Should().ContainSingle().Subject;
            job.Command.Should().Be("train --hours {hours}");
            job.DefaultHours.Should().Be(6);
            job.SelfLimiting.Should().BeTrue();
            job.Missing.Should().BeNull("nothing has checked the PC for this job yet");
            queue.Generated.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MissingSnapshot_DegradesToEmptyInsteadOfThrowing()
    {
        var store = new GlobalDataStore(Path.Combine(Path.GetTempPath(), "po-global-missing", Guid.NewGuid().ToString("N")));

        store.LoadFleetView().Repos.Should().BeEmpty();
        store.LoadVault().Brains.Should().BeEmpty();
        store.LoadReadiness().Apps.Should().BeEmpty();
        store.LoadRl().Repos.Should().BeEmpty();
        store.LoadSummary().RepoCount.Should().Be(0);
        store.LoadBriefing().IsBaseline.Should().BeTrue();
        store.LoadUnite().Creatures.Should().BeEmpty();
    }

    [Fact]
    public void CorruptSnapshot_DegradesToEmptyInsteadOfThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-corrupt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), "{ this is not json");
            var store = new GlobalDataStore(dir);

            store.LoadFleetView().Repos.Should().BeEmpty();
            store.LoadSummary().RepoCount.Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Briefing_ComputesTheDeltaFromTwoCrawls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-briefing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "history"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), """{"generated":"2026-01-02T00:00:00Z","repos":{}}""");
            File.WriteAllLines(Path.Combine(dir, "history", "station_timeline.jsonl"),
            [
                """{"when":"2026-01-01T00:00:00Z","stamp":"a","repos":{"PoDemo":{"bytes":100,"dirty":0,"commits_total":10,"onnx_shas":["aa"],"ci":"success"}}}""",
                """{"when":"2026-01-02T00:00:00Z","stamp":"b","repos":{"PoDemo":{"bytes":400000000,"dirty":2,"commits_total":13,"onnx_shas":["aa","bb","cc"],"ci":"failure"}}}""",
            ]);

            var briefing = new GlobalDataStore(dir).LoadBriefing();

            briefing.IsBaseline.Should().BeFalse();
            briefing.Commits.Should().Be(3);
            briefing.NewBrains.Should().Be(2);
            briefing.MovedBytes.Should().BePositive();
            briefing.Items.Should().Contain(i => i.Kind == "ci" && i.Text.Contains("failure", StringComparison.Ordinal));
            briefing.Items.Should().Contain(i => i.Kind == "dirty");
            briefing.Items.Should().Contain(i => i.Kind == "disk");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Vault_StarsBrainsListedInTheGoldFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-gold", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), """
                {
                  "generated": "2026-01-01T00:00:00Z",
                  "repos": {
                    "PoDemo": {
                      "name": "PoDemo",
                      "kind": "unity",
                      "is_rl": true,
                      "git": { "is_git": true },
                      "onnx": [
                        { "rel": "PoDemo/a.onnx", "name": "a.onnx", "kb": 10.0, "shipped": true, "obs": 131, "act": 28 },
                        { "rel": "PoDemo/b.onnx", "name": "b.onnx", "kb": 10.0, "shipped": true, "obs": 131, "act": 28 }
                      ]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(dir, "config", "vault_gold.json"),
                """{"gold":{"PoDemo/a.onnx":true}}""");

            var vault = new GlobalDataStore(dir).LoadVault();

            vault.BrainCount.Should().Be(2);
            vault.GoldCount.Should().Be(1);
            vault.Brains.Single(b => b.Name == "a.onnx").Gold.Should().BeTrue();
            vault.Brains.Single(b => b.Name == "b.onnx").Gold.Should().BeFalse();
            vault.Sockets.Should().ContainSingle().Which.Socket.Should().Be("131 → 28");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Readiness_PrefersASetStageOverTheSuggestion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), """
                {
                  "generated": "2026-01-01T00:00:00Z",
                  "repos": {
                    "PoDemo": {
                      "name": "PoDemo",
                      "kind": "unity",
                      "git": { "is_git": true, "dirty_count": 0 },
                      "ci": { "latest_run": { "conclusion": "success" } },
                      "docs": { "live_url": "https://example.test", "tests": ["tests"] },
                      "media": { "count": 9 }
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(dir, "config", "readiness.json"),
                """{"apps":{"PoDemo":{"stage":"shipped","checklist":{"core_loop":true,"controls":true}}}}""");

            var app = new GlobalDataStore(dir).LoadReadiness().Apps.Should().ContainSingle().Subject;

            app.StageIsSet.Should().BeTrue();
            app.Stage.Should().Be("shipped");
            app.Effective.Should().Be("Shipped");
            app.Done.Should().Be(2);
            app.Total.Should().Be(6);
            app.Suggested.Should().Be("playable");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
