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

    public GlobalSnapshotDto LoadFleet() =>
        Load("fleet.json", GlobalJsonContext.Default.GlobalSnapshotDto, static () => new GlobalSnapshotDto());

    public GlobalUniteDto LoadUnite() =>
        Load("unite.json", GlobalJsonContext.Default.GlobalUniteDto, static () => new GlobalUniteDto());

    public GlobalTrainJobsFileDto LoadTrainJobs() =>
        Load(Path.Combine("config", "train_jobs.json"), GlobalJsonContext.Default.GlobalTrainJobsFileDto,
            static () => new GlobalTrainJobsFileDto());

    public List<GlobalLessonDto> LoadLessons()
    {
        var harvested = Load("lessons.json", GlobalJsonContext.Default.GlobalLessonsFileDto,
            static () => new GlobalLessonsFileDto()).Lessons;
        var manual = Load(Path.Combine("config", "lessons_manual.json"), GlobalJsonContext.Default.GlobalLessonsFileDto,
            static () => new GlobalLessonsFileDto()).Lessons;

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

    public List<GlobalTimelineEntryDto> LoadStationTimeline() =>
        LoadJsonLines(Path.Combine("history", "station_timeline.jsonl"),
            GlobalJsonContext.Default.GlobalTimelineEntryDto);

    public List<GlobalRlTimelineEntryDto> LoadRlTimeline() =>
        LoadJsonLines(Path.Combine("rl", "rl_timeline.jsonl"), GlobalJsonContext.Default.GlobalRlTimelineEntryDto);

    public Dictionary<string, GlobalRlRepoFactDto> LoadRlFacts() =>
        Load("rl_facts.json",
            (JsonTypeInfo<Dictionary<string, GlobalRlRepoFactDto>>)GlobalJsonContext.Default.DictionaryStringGlobalRlRepoFactDto,
            static () => []);

    // ─── Assembled responses ────────────────────────────────────────────────

    public GlobalSummaryDto LoadSummary()
    {
        var fleet = LoadFleet();
        var jobs = LoadTrainJobs().Jobs;
        var rlTimeline = LoadRlTimeline();
        var lessons = LoadLessons();
        var unite = LoadUnite();
        var lastRl = rlTimeline.Count > 0 ? rlTimeline[^1] : null;
        var repos = fleet.Repos;

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
        var gold = Load(Path.Combine("config", "vault_gold.json"), GlobalJsonContext.Default.GlobalVaultGoldFileDto,
            static () => new GlobalVaultGoldFileDto()).Gold;

        var brains = new List<GlobalVaultBrainDto>();
        var models = new List<GlobalVaultModelDto>();

        foreach (var repo in fleet.Repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
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
        var config = Load(Path.Combine("config", "readiness.json"), GlobalJsonContext.Default.GlobalReadinessFileDto,
            static () => new GlobalReadinessFileDto()).Apps;

        var apps = new List<GlobalReadinessAppDto>();
        foreach (var repo in fleet.Repos.Where(r => r.Git.IsGit).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            config.TryGetValue(repo.Name, out var entry);
            entry ??= new GlobalReadinessEntryDto();

            var checklist = Checks
                .Select(c => new GlobalChecklistItemDto
                {
                    Key = c.Key,
                    Label = c.Label,
                    Done = entry.Checklist.TryGetValue(c.Key, out var done) && done,
                })
                .ToList();
            var doneCount = checklist.Count(c => c.Done);

            var live = repo.Docs.LiveUrl ?? "";
            var ci = repo.Ci.LatestRun?.Conclusion ?? "";
            var media = repo.Media.Count;

            var (suggested, why) =
                doneCount == Checks.Length ? ("ready", "all 6 checklist items ticked")
                : doneCount >= 4 ? ("polishing", $"{doneCount}/6 checklist items ticked")
                : live.Length > 0 && string.Equals(ci, "success", StringComparison.OrdinalIgnoreCase)
                    ? ("playable", "deployed (live URL in README) and CI is green")
                    : live.Length > 0 ? ("playable", "has a live URL")
                    : media >= 5 ? ("playable", "has a capture reel")
                    : ("prototype", "no live URL and few captures");

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
            });
        }

        return new GlobalReadinessDto { Apps = apps };
    }

    public GlobalBriefingDto LoadBriefing()
    {
        var fleet = LoadFleet();
        var timeline = LoadStationTimeline();
        var failing = fleet.Repos.Where(r => IsCiConclusion(r, "failure")).Select(r => r.Name).ToList();

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

            var repoFleet = fleet.Repos.FirstOrDefault(r => r.Name == name);
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
                var inFleet = fleet.Repos.FirstOrDefault(r => string.Equals(r.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Oldest timeline entry this store will look at — the briefing compares two.</summary>
    public int TimelineEntries => LoadStationTimeline().Count;

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
