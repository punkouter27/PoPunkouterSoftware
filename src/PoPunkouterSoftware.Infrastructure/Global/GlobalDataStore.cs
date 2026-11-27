using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Reads the GLOBAL snapshot the crawler in <c>scripts/global</c> writes to
/// <c>App_Data/global</c>, and assembles the responses behind <c>/api/global/*</c>.
/// </summary>
/// <remarks>
/// The directory is server-side only — deliberately under the content root, never the web
/// root — because it carries local paths, commit hashes and working-tree state. Every read
/// degrades: a missing, half-written or stale file yields an empty shape, never an exception,
/// so one failed crawl cannot take the pages down. Values are cached per file until its
/// timestamp or size changes, which is what lets a locally re-run crawl appear on reload.
/// </remarks>
public sealed class GlobalDataStore
{
    private static readonly string[] Stages = ["prototype", "playable", "polishing", "ready", "shipped"];

    private static readonly Dictionary<string, string> StageLabels = new(StringComparer.Ordinal)
    {
        ["prototype"] = "Prototype",
        ["playable"] = "Playable",
        ["polishing"] = "Polishing",
        ["ready"] = "Ready to ship",
        ["shipped"] = "Shipped",
    };

    private static readonly (string Key, string Label)[] Checks =
    [
        ("core_loop", "Core loop is fun and complete"),
        ("controls", "Controls / input feel right"),
        ("ui_polish", "UI polished (home corners, /diag, phone width)"),
        ("audio_fx", "Audio & FX in place"),
        ("perf", "Runs smoothly on the target (web / mobile / desktop)"),
        ("no_critical_bugs", "No known critical bugs"),
    ];

    private readonly string _dir;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dataDirectory">Usually <c>App_Data/global</c> under the content root.</param>
    public GlobalDataStore(string dataDirectory) => _dir = dataDirectory;

    private sealed record CacheEntry(DateTime LastWriteUtc, long Length, object Value);

    // ─── Raw snapshot reads ─────────────────────────────────────────────────

    public GlobalSnapshotDto LoadFleet()
    {
        var fleet = Load("fleet.json", GlobalCrawlerJsonContext.Default.GlobalSnapshotDto, static () => new GlobalSnapshotDto());
        fleet.Repos ??= [];
        foreach (var (name, repo) in fleet.Repos)
            Normalize(repo, name);

        return fleet;
    }

    /// <summary>
    /// The crawl writes <c>null</c> for whole sections of a folder that is not a repo
    /// (<c>ci</c>, <c>docs</c>, <c>media</c>, <c>recent_commits</c>), and a JSON <c>null</c>
    /// overwrites a property initializer. Fill every one back in once, here, so no page and no
    /// aggregate has to null-check a section.
    /// </summary>
    private static void Normalize(GlobalRepoDto repo, string name)
    {
        repo.Name = string.IsNullOrEmpty(repo.Name) ? name : repo.Name;
        repo.Kind ??= "";

        repo.Git ??= new GlobalGitDto();
        repo.Git.DirtyFiles ??= [];
        repo.Git.RecentCommits ??= [];

        repo.Ci ??= new GlobalCiDto();
        repo.Ci.Workflows ??= [];
        if (repo.Ci.LatestRun is { } run)
            run.Recent ??= [];

        repo.Docs ??= new GlobalDocsDto();
        repo.Docs.Files ??= [];
        repo.Docs.Tests ??= [];

        repo.Media ??= new GlobalMediaDto();
        repo.Media.Newest ??= [];
        repo.Media.Showcase ??= [];

        repo.Onnx ??= [];
        repo.Mjcf ??= [];

        repo.Disk ??= new GlobalDiskDto();
        repo.Disk.Categories ??= [];
        repo.Disk.HeavyDirs ??= [];
    }

    /// <summary>Every repo in the crawl, ordered by name, with <see cref="GlobalRepoDto.Name"/> set.</summary>
    public GlobalFleetDto LoadFleetView()
    {
        var fleet = LoadFleet();
        return new GlobalFleetDto
        {
            Generated = fleet.Generated,
            Stamp = fleet.Stamp,
            UsedGh = fleet.UsedGh,
            Repos = fleet.Repos
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Value)
                .ToList(),
        };
    }

    public GlobalUniteDto LoadUnite()
    {
        var unite = Load("unite.json", GlobalJsonContext.Default.GlobalUniteDto, static () => new GlobalUniteDto());
        unite.Rules ??= new GlobalUniteRulesDto();
        unite.Rules.Markdown ??= "";
        unite.Documents ??= [];
        unite.Creatures ??= [];
        foreach (var creature in unite.Creatures)
            creature.Files ??= [];

        return unite;
    }

    public GlobalTrainJobsFileDto LoadTrainJobs()
    {
        var jobs = Load(Path.Combine("config", "train_jobs.json"), GlobalCrawlerJsonContext.Default.GlobalTrainJobsFileDto,
            static () => new GlobalTrainJobsFileDto());
        jobs.Jobs ??= [];
        foreach (var job in jobs.Jobs)
            job.Requires ??= [];

        return jobs;
    }

    /// <summary>
    /// The training queue: <c>queue.json</c> (jobs plus which required paths were missing at
    /// the last crawl) when the crawler has written it, else the bare job list.
    /// </summary>
    public GlobalQueueDto LoadQueue()
    {
        var queue = Load("queue.json", GlobalCrawlerJsonContext.Default.GlobalQueueDto,
            () => new GlobalQueueDto { Jobs = LoadTrainJobs().Jobs });
        queue.Jobs ??= [];
        queue.CloudJobs ??= [];
        queue.Behaviors ??= [];
        queue.PastRuns ??= [];
        foreach (var job in queue.Jobs)
            job.Requires ??= [];

        return queue;
    }

    /// <summary>The standards prose the crawl copied from <c>docs/global-standards</c>.</summary>
    public GlobalStandardsDto LoadStandards()
    {
        var standards = Load("standards.json", GlobalCrawlerJsonContext.Default.GlobalStandardsDto,
            static () => new GlobalStandardsDto());
        standards.Documents ??= [];
        return standards;
    }

    public List<GlobalLessonDto> LoadLessons()
    {
        var harvested = Load("lessons.json", GlobalCrawlerJsonContext.Default.GlobalLessonsFileDto,
            static () => new GlobalLessonsFileDto()).Lessons ?? [];
        var manual = Load(Path.Combine("config", "lessons_manual.json"), GlobalCrawlerJsonContext.Default.GlobalLessonsFileDto,
            static () => new GlobalLessonsFileDto()).Lessons ?? [];

        foreach (var lesson in harvested)
        {
            lesson.Repos ??= [];
            lesson.Tags ??= [];
        }

        if (manual.Count == 0)
            return harvested;

        // Manual entries are the owner's own; a duplicate of a harvested bullet is dropped.
        var seen = harvested.Select(l => l.Text.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = new List<GlobalLessonDto>(harvested);
        for (var i = 0; i < manual.Count; i++)
        {
            var m = manual[i];
            if (string.IsNullOrWhiteSpace(m.Text) || !seen.Add(m.Text.Trim()))
                continue;

            var repo = string.IsNullOrWhiteSpace(m.Repo) ? "fleet" : m.Repo;
            merged.Add(new GlobalLessonDto
            {
                Id = $"manual{i}",
                Repo = repo,
                Repos = [repo],
                File = m.File,
                Line = m.Line,
                Heading = string.IsNullOrWhiteSpace(m.Heading) ? "Manual entry" : m.Heading,
                Kind = string.IsNullOrWhiteSpace(m.Kind) ? "trap" : m.Kind,
                Text = m.Text,
                Tags = m.Tags,
                Manual = true,
            });
        }

        return merged;
    }

    public List<GlobalTimelineEntryDto> LoadStationTimeline()
    {
        var entries = LoadJsonLines(Path.Combine("history", "station_timeline.jsonl"),
            GlobalCrawlerJsonContext.Default.GlobalTimelineEntryDto);
        foreach (var entry in entries)
        {
            entry.Repos ??= [];
            foreach (var repo in entry.Repos.Values)
            {
                repo.Cats ??= [];
                repo.OnnxShas ??= [];
            }
        }

        return entries;
    }

    public List<GlobalRlTimelineEntryDto> LoadRlTimeline()
    {
        var entries = LoadJsonLines(Path.Combine("rl", "rl_timeline.jsonl"),
            GlobalCrawlerJsonContext.Default.GlobalRlTimelineEntryDto);
        foreach (var entry in entries)
        {
            entry.PerRepoOnnx ??= [];
            entry.PerRepoSps ??= [];
        }

        return entries;
    }

    public Dictionary<string, GlobalRlRepoFactDto> LoadRlFacts()
    {
        var facts = Load("rl_facts.json",
            (JsonTypeInfo<Dictionary<string, GlobalRlRepoFactDto>>)GlobalCrawlerJsonContext.Default.DictionaryStringGlobalRlRepoFactDto,
            static () => []);
        foreach (var fact in facts.Values)
        {
            fact.YamlConfigs ??= [];
            fact.RewardConfigs ??= [];
            fact.Visuals ??= [];
            if (fact.Benchmark is { } benchmark)
            {
                benchmark.Net ??= [];
                benchmark.Versions ??= [];
            }
        }

        return facts;
    }

    // ─── Assembled responses ────────────────────────────────────────────────

    public GlobalSummaryDto LoadSummary()
    {
        var fleet = LoadFleet();
        var jobs = LoadTrainJobs().Jobs;
        var rlTimeline = LoadRlTimeline();
        var lessons = LoadLessons();
        var unite = LoadUnite();
        var lastRl = rlTimeline.Count > 0 ? rlTimeline[^1] : null;
        var repos = fleet.Repos.Values.ToList();

        var jobsReady = jobs.Count(j => j.Requires.Count == 0
            || j.Requires.All(r => repos.Any(repo => r.StartsWith(repo.Name + "/", StringComparison.OrdinalIgnoreCase))));
        var reclaimable = repos
            .SelectMany(r => r.Disk.HeavyDirs)
            .Where(h => string.Equals(h.Safety, "regenerable", StringComparison.OrdinalIgnoreCase))
            .Sum(h => h.Bytes);

        return new GlobalSummaryDto
        {
            Generated = fleet.Generated,
            Stamp = fleet.Stamp,
            UsedGh = fleet.UsedGh,
            RepoCount = repos.Count,
            RlCount = repos.Count(r => r.IsRl),
            BrainCount = repos.Sum(r => r.Onnx.Count),
            ShippedBrainCount = repos.Sum(r => r.Onnx.Count(b => b.Shipped)),
            ModelCount = repos.Sum(r => r.Mjcf.Count),
            CiFailing = repos.Count(r => IsCiConclusion(r, "failure")),
            DirtyRepos = repos.Count(r => r.Git.IsGit && r.Git.DirtyCount > 0),
            DiskBytes = repos.Sum(r => r.Disk.TotalBytes ?? 0),
            ReclaimableBytes = reclaimable,
            LessonsCount = lessons.Count,
            CreaturesCount = unite.Creatures.Count,
            JobsReady = jobsReady,
            JobsTotal = jobs.Count,
            ShippedBrainsRl = lastRl?.TotalShippedOnnx ?? 0,
            PeakSps = lastRl?.PeakSps ?? 0,
            RlRecordedAt = lastRl?.Timestamp,
            UniteGenerated = unite.Generated,
            MediaCount = repos.Sum(r => r.Media.Count),
        };
    }

    public GlobalVaultDto LoadVault()
    {
        var fleet = LoadFleet();
        var gold = Load(Path.Combine("config", "vault_gold.json"), GlobalCrawlerJsonContext.Default.GlobalVaultGoldFileDto,
            static () => new GlobalVaultGoldFileDto()).Gold ?? [];

        var brains = new List<GlobalVaultBrainDto>();
        var models = new List<GlobalVaultModelDto>();

        foreach (var repo in fleet.Repos.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var b in repo.Onnx)
            {
                var isGold = gold.ContainsKey(b.Rel) || gold.ContainsKey(b.Name)
                    || (b.Sha1 is { Length: > 0 } sha && gold.ContainsKey(sha));
                brains.Add(new GlobalVaultBrainDto
                {
                    Repo = repo.Name,
                    Name = b.Name,
                    Rel = b.Rel,
                    Kb = b.Kb,
                    Shipped = b.Shipped,
                    Gold = isGold,
                    Obs = b.Obs,
                    Act = b.Act,
                    Socket = Socket(b.Obs, b.Act),
                    Modified = DateTimeOffset.FromUnixTimeSeconds(b.Mtime),
                });
            }

            foreach (var m in repo.Mjcf)
            {
                models.Add(new GlobalVaultModelDto
                {
                    Repo = repo.Name,
                    Name = m.Name,
                    Rel = m.Rel,
                    Model = m.Model,
                    Bodies = m.Bodies,
                    Joints = m.Joints,
                    Actuators = m.Actuators,
                    Timestep = m.Timestep,
                });
            }
        }

        var sockets = brains
            .GroupBy(b => new { b.Socket, Obs = b.Obs ?? 0, Act = b.Act ?? 0 })
            .Select(g => new GlobalSocketDto
            {
                Socket = g.Key.Socket,
                Obs = g.Key.Obs,
                Act = g.Key.Act,
                Count = g.Count(),
            })
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Socket, StringComparer.Ordinal)
            .ToList();

        return new GlobalVaultDto
        {
            BrainCount = brains.Count,
            GoldCount = brains.Count(b => b.Gold),
            ModelCount = models.Count,
            Sockets = sockets,
            Brains = brains
                .OrderBy(b => b.Repo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Models = models,
        };
    }

    public GlobalReadinessDto LoadReadiness()
    {
        var fleet = LoadFleet();
        var config = Load(Path.Combine("config", "readiness.json"), GlobalCrawlerJsonContext.Default.GlobalReadinessFileDto,
            static () => new GlobalReadinessFileDto()).Apps ?? [];
        foreach (var entry in config.Values)
            entry.Checklist ??= [];

        var apps = new List<GlobalReadinessAppDto>();
        foreach (var repo in fleet.Repos.Values.Where(r => r.Git.IsGit).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            config.TryGetValue(repo.Name, out var entry);
            entry ??= new GlobalReadinessEntryDto();

            var checklist = Checks
                .Select(c => new GlobalChecklistItemDto
                {
                    Key = c.Key,
                    Label = c.Label,
                    Done = entry.Checklist.TryGetValue(c.Key, out var done) && done,
                    Value = entry.Checklist.TryGetValue(c.Key, out var value) ? value : null,
                })
                .ToList();
            var doneCount = checklist.Count(c => c.Done);

            var live = repo.Docs.LiveUrl ?? "";
            var ci = repo.Ci.LatestRun?.Conclusion ?? "";
            var media = repo.Media.Count;

            var (suggested, why) = GlobalReadinessRules.Suggest(doneCount, Checks.Length, live, ci, media);

            var stageIsSet = entry.Stage is not null && Stages.Contains(entry.Stage);
            var stage = stageIsSet ? entry.Stage! : suggested;

            apps.Add(new GlobalReadinessAppDto
            {
                Name = repo.Name,
                Kind = repo.Kind,
                IsRl = repo.IsRl,
                Stage = stage,
                StageIsSet = stageIsSet,
                Suggested = suggested,
                Why = why,
                Effective = StageLabels[stage],
                Notes = entry.Notes,
                LiveUrl = repo.Docs.LiveUrl,
                Ci = ci,
                HasTests = repo.Docs.Tests.Count > 0,
                DaysSinceCommit = repo.Git.DaysSinceCommit,
                Dirty = repo.Git.DirtyCount,
                Done = doneCount,
                Total = Checks.Length,
                Checklist = checklist,
                MediaCount = media,
            });
        }

        return new GlobalReadinessDto { Apps = apps };
    }

    /// <summary>
    /// The hub page: what GLOBAL/index.html computed in hub_extras.py, from the same files.
    /// </summary>
    public GlobalHomeDto LoadHome()
    {
        var fleet = LoadFleet();
        var repos = fleet.Repos.Values.ToList();
        var gitRepos = repos.Where(r => r.Git.IsGit).ToList();
        var rl = LoadRl();
        var timeline = LoadStationTimeline();
        var trend = timeline.Select(Point).ToList();
        var queue = LoadQueue();

        var attention = new List<GlobalAttentionDto>();
        foreach (var repo in gitRepos.Where(r => r.Ci.LatestRun?.Conclusion is { Length: > 0 } c && c != "success"))
            attention.Add(new() { Severity = "fix", Text = $"CI is not passing in {repo.Name} ({repo.Ci.LatestRun!.Conclusion})", Href = "/global/fleet" });
        var noCi = gitRepos.Count(r => string.IsNullOrEmpty(r.Ci.LatestRun?.Conclusion));
        if (noCi > 0)
            attention.Add(new() { Severity = "check", Text = $"{noCi} repo(s) have no completed CI result", Href = "/global/fleet" });
        foreach (var repo in gitRepos)
        {
            if (repo.Git.DirtyCount > 0)
                attention.Add(new() { Severity = "check", Text = $"{repo.Name}: {repo.Git.DirtyCount} uncommitted file(s) - commit or stash", Href = "/global/fleet" });
            if (repo.Git.Ahead > 0)
                attention.Add(new() { Severity = "check", Text = $"{repo.Name}: {repo.Git.Ahead} commit(s) not pushed to GitHub", Href = "/global/fleet" });
            if (repo.Git.Behind > 0)
                attention.Add(new() { Severity = "info", Text = $"{repo.Name}: {repo.Git.Behind} commit(s) behind remote - pull", Href = "/global/fleet" });
        }

        var flags = RlFlags(rl.Repos);
        if (flags.Count > 0)
            attention.Add(new() { Severity = "check", Text = $"{flags.Count} RL repo(s) have speed numbers that are missing or not comparable", Href = "#flags" });

        var ready = queue.Jobs.Count(j => j.Missing is { Count: 0 });
        if (queue.Jobs.Count > 0)
            attention.Add(new() { Severity = "info", Text = $"Overnight GPU queue: {ready}/{queue.Jobs.Count} jobs ready - plan tonight's run", Href = "/global/queue" });

        var heavy = repos.SelectMany(r => r.Disk.HeavyDirs)
            .Where(h => string.Equals(h.Safety, "regenerable", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Bytes)
            .ToList();
        if (heavy.Count > 0)
        {
            var top = string.Join(", ", heavy.Take(3).Select(h => $"{h.Rel} ({h.Bytes / 1e9:0.0} GB)"));
            attention.Add(new()
            {
                Severity = "info",
                Text = $"Disk: {heavy.Sum(h => h.Bytes) / 1e9:0} GB can be rebuilt if you need space. Biggest: {top}. Cleanup commands are copy-only - nothing is deleted for you.",
                Href = "/global/disk",
            });
        }

        return new GlobalHomeDto
        {
            FleetGenerated = fleet.Generated,
            RlRecorded = rl.History.Count > 0 && DateTimeOffset.TryParse(rl.History[^1].Timestamp, out var recorded) ? recorded : null,
            Attention = attention,
            Benchmark = LoadBenchmarkRun(),
            Now = new GlobalTrendPointDto
            {
                When = fleet.Generated,
                Brains = gitRepos.Sum(r => r.Onnx.Count),
                DiskBytes = repos.Sum(r => r.Disk.TotalBytes ?? 0),
                Dirty = gitRepos.Count(r => r.Git.DirtyCount > 0),
                CiFailing = gitRepos.Count(r => r.Ci.LatestRun?.Conclusion is { Length: > 0 } c && c != "success"),
            },
            Previous = trend.Count > 1 ? trend[^2] : null,
            Trend = trend,
            Flags = flags,
            Benchmarks = rl.Repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            JobsReady = ready,
            JobsTotal = queue.Jobs.Count,
        };

        static GlobalTrendPointDto Point(GlobalTimelineEntryDto entry) => new()
        {
            When = entry.When,
            Brains = entry.Repos.Values.Sum(r => r.Onnx ?? 0),
            DiskBytes = entry.Repos.Values.Sum(r => r.Bytes ?? 0),
            Dirty = entry.Repos.Values.Count(r => r.Dirty > 0),
            CiFailing = entry.Repos.Values.Count(r => r.Ci is { Length: > 0 } c && c != "success"),
        };
    }

    /// <summary>The newest benchmark attempt (<c>benchmark_last.json</c>), or null when none was recorded.</summary>
    public GlobalBenchmarkRunDto? LoadBenchmarkRun()
    {
        var run = Load("benchmark_last.json", GlobalCrawlerJsonContext.Default.GlobalBenchmarkRunDto,
            static () => new GlobalBenchmarkRunDto());
        if (run.Stamp.Length == 0)
            return null;

        run.Results ??= [];
        return run;
    }

    /// <summary>RL speed numbers that are real but must not be compared at face value (hub_extras.rl_flags).</summary>
    public static List<GlobalRlFlagDto> RlFlags(IEnumerable<GlobalRlRepoViewDto> repos, DateTimeOffset? now = null)
    {
        var flags = new List<GlobalRlFlagDto>();
        foreach (var repo in repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (repo.Benchmark is not { } bench)
            {
                flags.Add(new() { Repo = repo.Name, Why = "no speed benchmark recorded - speed shows as '-' (not zero)" });
                continue;
            }

            var notes = new List<string>();
            if (bench.NumEnvs is > 0 and < 64)
                notes.Add($"only {bench.NumEnvs} envs (tiny test run)");
            if (bench.EnvStepsPerS is > 0 and < 1000)
                notes.Add($"{bench.EnvStepsPerS:N0} steps/s is very low");
            if (bench.When is { } when && ((now ?? DateTimeOffset.UtcNow) - when).TotalDays > 3)
                notes.Add($"benchmark is {(int)((now ?? DateTimeOffset.UtcNow) - when).TotalDays} days old");
            if (notes.Count > 0)
                flags.Add(new() { Repo = repo.Name, Why = string.Join("; ", notes) + " - not comparable with the 4,096-env runs" });
        }

        return flags;
    }

    public GlobalBriefingDto LoadBriefing()
    {
        var fleet = LoadFleet();
        var timeline = LoadStationTimeline();
        var failing = fleet.Repos.Values.Where(r => IsCiConclusion(r, "failure")).Select(r => r.Name).ToList();

        if (timeline.Count == 0)
        {
            return new GlobalBriefingDto
            {
                Generated = fleet.Generated,
                IsBaseline = true,
                CiFailing = failing,
            };
        }

        var current = timeline[^1];
        var previous = timeline.Count > 1 ? timeline[^2] : null;
        var items = new List<GlobalBriefingItemDto>();
        var repos = new List<GlobalBriefingRepoDto>();
        var commits = 0;
        var newBrains = 0;
        var moved = 0L;

        foreach (var (name, now) in current.Repos.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var before = previous is not null && previous.Repos.TryGetValue(name, out var hit) ? hit : null;
            var commitDelta = before?.CommitsTotal is { } was && now.CommitsTotal is { } isNow
                ? Math.Max(0, isNow - was)
                : 0;

            var beforeShas = before?.OnnxShas.ToHashSet(StringComparer.Ordinal) ?? [];
            var fresh = now.OnnxShas.Where(sha => !beforeShas.Contains(sha)).ToList();

            var diskDelta = (now.Bytes ?? 0) - (before?.Bytes ?? 0);
            var dirtyBefore = before?.Dirty ?? 0;
            var dirtyAfter = now.Dirty ?? 0;

            commits += commitDelta;
            newBrains += fresh.Count;
            if (Math.Abs(diskDelta) > 200_000_000)
                moved += Math.Abs(diskDelta);

            var repoFleet = fleet.Repos.Values.FirstOrDefault(r => r.Name == name);
            var latestMessages = repoFleet?.Git.RecentCommits
                .Take(3)
                .Select(c => c.Msg ?? "")
                .Where(m => m.Length > 0)
                .ToList() ?? [];

            if (commitDelta > 0)
            {
                items.Add(new GlobalBriefingItemDto
                {
                    Kind = "commits",
                    Repo = name,
                    Text = $"{commitDelta} commit{(commitDelta == 1 ? "" : "s")}",
                    Detail = string.Join("; ", latestMessages.Select(m => Truncate(m, 70))),
                });
            }

            if (fresh.Count > 0)
            {
                items.Add(new GlobalBriefingItemDto
                {
                    Kind = "brains",
                    Repo = name,
                    Text = $"{fresh.Count} new or changed brain{(fresh.Count == 1 ? "" : "s")}",
                });
            }

            if (Math.Abs(diskDelta) > 200_000_000)
            {
                items.Add(new GlobalBriefingItemDto
                {
                    Kind = "disk",
                    Repo = name,
                    Text = $"disk {(diskDelta > 0 ? "+" : "−")}{FormatBytes(Math.Abs(diskDelta))}",
                });
            }

            var ciBefore = before?.Ci;
            var ciAfter = now.Ci;
            if (!string.IsNullOrEmpty(ciAfter) && !string.Equals(ciBefore, ciAfter, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new GlobalBriefingItemDto
                {
                    Kind = "ci",
                    Repo = name,
                    Text = $"CI {ciBefore ?? "—"} → {ciAfter}",
                });
            }

            if (dirtyBefore != dirtyAfter)
            {
                items.Add(new GlobalBriefingItemDto
                {
                    Kind = "dirty",
                    Repo = name,
                    Text = $"uncommitted files {dirtyBefore} → {dirtyAfter}",
                });
            }

            if (commitDelta > 0 || fresh.Count > 0 || Math.Abs(diskDelta) > 200_000_000 || dirtyBefore != dirtyAfter)
            {
                repos.Add(new GlobalBriefingRepoDto
                {
                    Repo = name,
                    Commits = commitDelta,
                    NewBrains = fresh.Count,
                    DiskDelta = diskDelta,
                    CiBefore = ciBefore,
                    CiAfter = ciAfter,
                    DirtyBefore = dirtyBefore,
                    DirtyAfter = dirtyAfter,
                    LatestCommitMessages = latestMessages,
                });
            }
        }

        return new GlobalBriefingDto
        {
            Generated = current.When ?? fleet.Generated,
            Since = previous?.When,
            IsBaseline = previous is null,
            Commits = commits,
            NewBrains = newBrains,
            MovedBytes = (long)Math.Min(int.MaxValue, moved),
            CiFailing = failing,
            Items = items,
            Repos = repos,
        };
    }

    public GlobalRlDto LoadRl()
    {
        var facts = LoadRlFacts();
        var history = LoadRlTimeline();
        var fleet = LoadFleet();
        var archetypes = TypicalStacks();

        var repos = facts
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var fact = kv.Value;
                var inFleet = fleet.Repos.Values.FirstOrDefault(r => string.Equals(r.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new GlobalRlRepoViewDto
                {
                    Name = kv.Key,
                    Shipped = fact.OnnxShippedCount,
                    Found = fact.OnnxTotalFound,
                    Mjcf = fact.MjcfCount,
                    RewardConfigs = fact.RewardConfigs.Count,
                    YamlConfigs = fact.YamlConfigs.Count,
                    Visuals = fact.Visuals.Count,
                    Benchmark = fact.Benchmark,
                    Stack = inFleet?.RlStack is { Length: > 0 } stack ? stack : archetypes.GetValueOrDefault(kv.Key, ""),
                    InFleet = inFleet is not null,
                };
            })
            .ToList();

        var last = history.Count > 0 ? history[^1] : null;
        return new GlobalRlDto
        {
            Repos = repos,
            History = history,
            ShippedBrains = last?.TotalShippedOnnx ?? repos.Sum(r => r.Shipped),
            AllBrains = last?.TotalAllOnnx ?? repos.Sum(r => r.Found),
            PeakSps = last?.PeakSps ?? 0,
            AvgSps = last?.AvgSps ?? 0,
            EstHours = last?.EstTotalHours ?? 0,
            EstStepsB = last?.EstTotalStepsB ?? 0,
            Updated7d = last?.Updated7dOnnx ?? 0,
            RecordedAt = last?.Timestamp,
        };
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static Dictionary<string, string> TypicalStacks() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PoBasketball"] = "Unreal + MuJoCo",
        ["PoRaceUnreal"] = "Unreal + MuJoCo",
        ["PoRobotTrain"] = "Unreal + MuJoCo",
        ["PoSlapUnreal"] = "Unreal + MuJoCo",
        ["PoBox"] = "Unity + MuJoCo",
        ["PoHillKing"] = "Unity + MuJoCo",
        ["PoOlympic"] = "Unity + MuJoCo",
        ["PoFootball"] = "Unity ML-Agents",
        ["PoSoccer"] = "Unity ML-Agents",
        ["PoSumo"] = "Unity ML-Agents",
    };

    private static bool IsCiConclusion(GlobalRepoDto repo, string conclusion) =>
        string.Equals(repo.Ci.LatestRun?.Conclusion, conclusion, StringComparison.OrdinalIgnoreCase);

    private static string Socket(int? obs, int? act) =>
        obs is > 0 && act is > 0 ? $"{obs} → {act}" : "unknown";

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : string.Concat(value.AsSpan(0, length), "…");

    public static string FormatBytes(long bytes)
    {
        double value = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private T Load<T>(string relativePath, JsonTypeInfo<T> typeInfo, Func<T> fallback)
    {
        var path = Path.Combine(_dir, relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            return fallback();

        if (_cache.TryGetValue(relativePath, out var hit)
            && hit.LastWriteUtc == info.LastWriteTimeUtc
            && hit.Length == info.Length
            && hit.Value is T cached)
        {
            return cached;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var value = JsonSerializer.Deserialize(stream, typeInfo);
            if (value is null)
                return fallback();

            _cache[relativePath] = new CacheEntry(info.LastWriteTimeUtc, info.Length, value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return fallback();
        }
    }

    private List<T> LoadJsonLines<T>(string relativePath, JsonTypeInfo<T> typeInfo)
    {
        var path = Path.Combine(_dir, relativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            return [];

        if (_cache.TryGetValue(relativePath, out var hit)
            && hit.LastWriteUtc == info.LastWriteTimeUtc
            && hit.Length == info.Length
            && hit.Value is List<T> cached)
        {
            return cached;
        }

        var items = new List<T>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var item = JsonSerializer.Deserialize(line, typeInfo);
                if (item is not null)
                    items.Add(item);
            }

            _cache[relativePath] = new CacheEntry(info.LastWriteTimeUtc, info.Length, items);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return items;
        }

        return items;
    }
}
