using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Shared;

// ─────────────────────────────────────────────────────────────────────────────
// Contracts behind /api/global/*. The data is a snapshot the crawler in
// scripts/global writes to App_Data/global; every type below is camelCase on the
// wire and defaults to an empty shape so a missing or half-written file degrades
// to "nothing to show" instead of a 500.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One crawl of the whole `_CODEMATT` workspace (<c>fleet.json</c>).</summary>
public sealed class GlobalSnapshotDto
{
    public DateTimeOffset? Generated { get; set; }
    public string? Stamp { get; set; }
    public bool UsedGh { get; set; }

    /// <summary>Keyed by repo folder name in the file; the fleet endpoint returns a list.</summary>
    public Dictionary<string, GlobalRepoDto> Repos { get; set; } = [];
}

/// <summary>The same crawl as an ordered list, which is what the pages consume.</summary>
public sealed class GlobalFleetDto
{
    public DateTimeOffset? Generated { get; set; }
    public string? Stamp { get; set; }
    public bool UsedGh { get; set; }
    public List<GlobalRepoDto> Repos { get; set; } = [];
}

public sealed class GlobalRepoDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsRl { get; set; }
    public string? RlStack { get; set; }
    public string? UnityVersion { get; set; }
    public GlobalGitDto Git { get; set; } = new();
    public GlobalCiDto Ci { get; set; } = new();
    public GlobalDocsDto Docs { get; set; } = new();
    public GlobalMediaDto Media { get; set; } = new();
    public List<GlobalBrainDto> Onnx { get; set; } = [];
    public List<GlobalModelDto> Mjcf { get; set; } = [];
    public GlobalDiskDto Disk { get; set; } = new();
}

public sealed class GlobalGitDto
{
    public bool IsGit { get; set; }
    public string? Branch { get; set; }
    public int DirtyCount { get; set; }
    public List<string> DirtyFiles { get; set; } = [];
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public DateTimeOffset? LastCommitIso { get; set; }
    public string? LastCommitMsg { get; set; }
    public string? LastCommitHash { get; set; }
    public double? DaysSinceCommit { get; set; }

    // SnakeCaseLower leaves a digit fused to the preceding word ("Commits7d"), while the
    // crawler writes "commits_7d"; these two names need to be explicit.
    [JsonPropertyName("commits_7d")]
    public int Commits7d { get; set; }

    [JsonPropertyName("commits_30d")]
    public int Commits30d { get; set; }
    public int CommitsTotal { get; set; }
    public string? Remote { get; set; }
    public string? Slug { get; set; }
    public List<GlobalCommitDto> RecentCommits { get; set; } = [];
}

public sealed class GlobalCommitDto
{
    public DateTimeOffset? Iso { get; set; }
    public string? Hash { get; set; }
    public string? Msg { get; set; }
}

public sealed class GlobalCiDto
{
    public List<string> Workflows { get; set; } = [];
    public GlobalCiRunDto? LatestRun { get; set; }
}

public sealed class GlobalCiRunDto
{
    public string? Conclusion { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? Event { get; set; }
    public string? HeadBranch { get; set; }
    public string? Status { get; set; }
    public string? Url { get; set; }
    public string? WorkflowName { get; set; }
    public List<string> Recent { get; set; } = [];
}

public sealed class GlobalDocsDto
{
    public Dictionary<string, bool> Files { get; set; } = [];
    public List<string> Tests { get; set; } = [];
    public string? LiveUrl { get; set; }
    public bool BenchmarkPs1 { get; set; }
    public bool BenchmarkHistory { get; set; }
}

public sealed class GlobalMediaDto
{
    public int Count { get; set; }
    public int GifCount { get; set; }
    public List<GlobalMediaItemDto> Newest { get; set; } = [];
    public List<GlobalMediaItemDto> Showcase { get; set; } = [];
}

public sealed class GlobalMediaItemDto
{
    public string Rel { get; set; } = "";
    public string? Ext { get; set; }
    public double Kb { get; set; }
    public long Mtime { get; set; }
}

public sealed class GlobalBrainDto
{
    public string Rel { get; set; } = "";
    public string Name { get; set; } = "";
    public double Kb { get; set; }
    public long Mtime { get; set; }
    public string? Sha1 { get; set; }
    public bool Shipped { get; set; }
    public int? Opset { get; set; }
    public int? Obs { get; set; }
    public int? Act { get; set; }

    /// <summary>ONNX graph inputs and outputs as the crawler wrote them: <c>[name, [dims…]]</c> pairs.</summary>
    public List<System.Text.Json.JsonElement> Inputs { get; set; } = [];
    public List<System.Text.Json.JsonElement> Outputs { get; set; } = [];
}

public sealed class GlobalModelDto
{
    public string Rel { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public int? Bodies { get; set; }
    public int? Joints { get; set; }
    public int? FreeJoints { get; set; }
    public int? Actuators { get; set; }
    public string? Timestep { get; set; }
    public int? Includes { get; set; }
    public long Mtime { get; set; }
}

public sealed class GlobalDiskDto
{
    public long? TotalBytes { get; set; }
    public Dictionary<string, long> Categories { get; set; } = [];
    public List<GlobalHeavyDirDto> HeavyDirs { get; set; } = [];
    public int ScanErrors { get; set; }
    public bool Complete { get; set; }
    public bool Stale { get; set; }
}

public sealed class GlobalHeavyDirDto
{
    public string Rel { get; set; } = "";
    public string? Category { get; set; }
    public string? Safety { get; set; }
    public long Bytes { get; set; }
    public int Files { get; set; }
}

// ─── Station readings ───────────────────────────────────────────────────────

public sealed class GlobalLessonsFileDto
{
    public int Count { get; set; }
    public List<GlobalLessonDto> Lessons { get; set; } = [];
}

public sealed class GlobalLessonDto
{
    public string? Id { get; set; }
    public string Repo { get; set; } = "";
    public List<string> Repos { get; set; } = [];
    public string? File { get; set; }
    public int Line { get; set; }
    public string? Heading { get; set; }
    public string Kind { get; set; } = "trap";
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool Manual { get; set; }
}

public sealed class GlobalReadinessFileDto
{
    public Dictionary<string, GlobalReadinessEntryDto> Apps { get; set; } = [];
}

public sealed class GlobalReadinessEntryDto
{
    public string? Stage { get; set; }
    public string? Notes { get; set; }
    public Dictionary<string, bool> Checklist { get; set; } = [];
}

public sealed class GlobalVaultGoldFileDto
{
    public Dictionary<string, bool> Gold { get; set; } = [];
}

public sealed class GlobalTrainJobsFileDto
{
    public List<GlobalTrainJobDto> Jobs { get; set; } = [];
}

public sealed class GlobalTrainJobDto
{
    public string? Id { get; set; }
    public string? Repo { get; set; }
    public string? Label { get; set; }
    public string? Cwd { get; set; }
    public string? Device { get; set; }
    public bool SelfLimiting { get; set; }
    public List<string> Requires { get; set; } = [];
    public string? BehaviorId { get; set; }
    public string? Command { get; set; }
    public double? DefaultHours { get; set; }
    public string? Source { get; set; }
    public string? After { get; set; }
    public string? StopFile { get; set; }

    /// <summary>
    /// The <see cref="Requires"/> paths that were absent on the owner's PC at the last crawl.
    /// Null when the crawl has not checked this job (no <c>queue.json</c> yet).
    /// </summary>
    public List<string>? Missing { get; set; }
}

/// <summary>
/// <c>/api/global/queue</c>: the training jobs from <c>config/train_jobs.json</c>, each with
/// the crawl's verdict on whether it could start (<c>queue.json</c>). Read-only: jobs run on
/// the owner's PC, never from this site.
/// </summary>
public sealed class GlobalQueueDto
{
    public DateTimeOffset? Generated { get; set; }
    public List<GlobalTrainJobDto> Jobs { get; set; } = [];

    /// <summary>The jobs <c>scripts/gcp/cloud_jobs.json</c> can run on the Google Cloud GPU.</summary>
    public List<GlobalCloudJobDto> CloudJobs { get; set; } = [];

    /// <summary>The RL triage list (<c>config/behaviors.json</c>), most urgent first.</summary>
    public List<GlobalBehaviorDto> Behaviors { get; set; } = [];

    /// <summary>The newest overnight queue runs on the PC (<c>scripts/global/.local/runs</c>), newest first.</summary>
    public List<GlobalQueueRunDto> PastRuns { get; set; } = [];
}

public sealed class GlobalBehaviorDto
{
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Behavior { get; set; } = "";
    public int Importance { get; set; }
    public int Quality { get; set; }

    /// <summary>importance × (100 − quality) / 10, rounded (the old GPU queue's ranking).</summary>
    public int Urgency { get; set; }
    public string? EstTime { get; set; }
    public bool QuickWin { get; set; }
    public string? GameRole { get; set; }
    public string? CurrentState { get; set; }
    public string? NextAction { get; set; }
}

public sealed class GlobalQueueRunDto
{
    public string Stamp { get; set; } = "";
    public string? Queue { get; set; }

    /// <summary>False when the run left no summary.json (still running, or stopped early).</summary>
    public bool HasSummary { get; set; }
    public List<GlobalQueueRunJobDto> Jobs { get; set; } = [];

    /// <summary>The last lines of queue.log.</summary>
    public List<string> Log { get; set; } = [];
}

public sealed class GlobalQueueRunJobDto
{
    public string? Id { get; set; }
    public string? Repo { get; set; }
    public string? Outcome { get; set; }
    public int Minutes { get; set; }
    public double Hours { get; set; }
    public List<string> NewOnnx { get; set; } = [];
}

public sealed class GlobalCloudJobDto
{
    public string Id { get; set; } = "";
    public string? Source { get; set; }
    public string? Cwd { get; set; }
    public string? Command { get; set; }
    public double? Hours { get; set; }
}

/// <summary><c>/api/global/standards</c>: the prose under <c>docs/global-standards</c>.</summary>
public sealed class GlobalStandardsDto
{
    public DateTimeOffset? Generated { get; set; }
    public List<GlobalStandardDocDto> Documents { get; set; } = [];
}

public sealed class GlobalStandardDocDto
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Group { get; set; } = "";
    public string Markdown { get; set; } = "";
}

/// <summary>
/// One request from a /global page to the helper on the owner's PC
/// (<c>scripts/global/local_helper.py</c>, <c>POST http://127.0.0.1:5050/api/action</c>).
/// The page only names things (a repo, job ids, hours); the helper owns every command.
/// </summary>
public sealed class LocalHelperRequest
{
    public string Action { get; set; } = "";
    public string? Repo { get; set; }
    public string? Job { get; set; }
    public string? Name { get; set; }
    public string? Brain { get; set; }
    public string? Target { get; set; }
    public string? Dest { get; set; }
    public string? Command { get; set; }
    public string? Id { get; set; }
    public double? Hours { get; set; }
    public bool? Dry { get; set; }
    public bool? Push { get; set; }

    /// <summary>With <see cref="Push"/>: commit and push the snapshot as it is (config edits), without crawling.</summary>
    public bool? NoCrawl { get; set; }

    /// <summary><c>save_config</c>: <c>readiness</c> or <c>vault_gold</c>.</summary>
    public string? Arg { get; set; }

    /// <summary><c>save_config</c>: the whole file, as the page edited it.</summary>
    public System.Text.Json.JsonElement? Data { get; set; }
    public int? CoolTemp { get; set; }
    public int? GapMin { get; set; }
    public bool? RefreshAfter { get; set; }
    public List<LocalHelperQueueItem>? Jobs { get; set; }
    public List<string>? Repos { get; set; }
}

public sealed class LocalHelperQueueItem
{
    public string Id { get; set; } = "";
    public double Hours { get; set; }
}

public sealed class LocalHelperResult
{
    public bool Ok { get; set; }
    public bool Authorized { get; set; }
    public string? Output { get; set; }

    /// <summary>Id of a background job the helper started; poll it with <c>job_status</c>.</summary>
    public string? Job { get; set; }
    public bool? Done { get; set; }
}

/// <summary>One line of <c>history/station_timeline.jsonl</c>.</summary>
public sealed class GlobalTimelineEntryDto
{
    public DateTimeOffset? When { get; set; }
    public string? Stamp { get; set; }
    public Dictionary<string, GlobalTimelineRepoDto> Repos { get; set; } = [];
}

public sealed class GlobalTimelineRepoDto
{
    public long? Bytes { get; set; }
    public Dictionary<string, long> Cats { get; set; } = [];
    public int? Dirty { get; set; }
    public int? CommitsTotal { get; set; }
    public string? LastHash { get; set; }
    public int? Onnx { get; set; }
    public List<string> OnnxShas { get; set; } = [];
    public int? Media { get; set; }
    public string? Ci { get; set; }
    public bool DiskStale { get; set; }
}

public sealed class GlobalRlRepoFactDto
{
    public GlobalBenchmarkDto? Benchmark { get; set; }
    public int OnnxTotalFound { get; set; }
    public int OnnxShippedCount { get; set; }
    public int MjcfCount { get; set; }
    public List<string> YamlConfigs { get; set; } = [];
    public List<string> RewardConfigs { get; set; } = [];
    public List<GlobalMediaItemDto> Visuals { get; set; } = [];
}

public sealed class GlobalBenchmarkDto
{
    public DateTimeOffset? When { get; set; }
    public bool Ok { get; set; }
    public string? MachineGpu { get; set; }
    public string? Trainer { get; set; }
    public string? Script { get; set; }
    public int? NumEnvs { get; set; }
    public int? Horizon { get; set; }
    public List<int> Net { get; set; } = [];
    public double? StepTimeMs { get; set; }
    public double? EnvStepsPerS { get; set; }
    /// <summary>python/torch/mujoco/... values, which the crawler writes as strings or numbers.</summary>
    public Dictionary<string, System.Text.Json.JsonElement> Versions { get; set; } = [];
    public int? TotalBenchmarkRuns { get; set; }
}

public sealed class GlobalRlTimelineEntryDto
{
    public string? Timestamp { get; set; }
    public int TotalShippedOnnx { get; set; }
    public int TotalAllOnnx { get; set; }

    [JsonPropertyName("updated_7d_onnx")]
    public int Updated7dOnnx { get; set; }
    public int PeakSps { get; set; }
    public int AvgSps { get; set; }
    public int EstTotalHours { get; set; }
    public double EstTotalStepsB { get; set; }
    public Dictionary<string, int> PerRepoOnnx { get; set; } = [];
    public Dictionary<string, int> PerRepoSps { get; set; } = [];
}

// ─── UNITE ──────────────────────────────────────────────────────────────────

public sealed class GlobalUniteDto
{
    public DateTimeOffset? Generated { get; set; }
    public GlobalUniteRulesDto Rules { get; set; } = new();
    public List<GlobalUniteDocumentDto> Documents { get; set; } = [];
    public List<GlobalCreatureDto> Creatures { get; set; } = [];
}

public sealed class GlobalUniteRulesDto
{
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public string Markdown { get; set; } = "";
}

public sealed class GlobalUniteDocumentDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
}

public sealed class GlobalCreatureDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? MorphologyId { get; set; }
    public int Joints { get; set; }
    public int RolesTotal { get; set; }
    public int RolesMapped { get; set; }
    public List<string> HeldOutside { get; set; } = [];
    public int? Obs { get; set; }
    public int? Action { get; set; }
    public double? Timestep { get; set; }
    public double? ControlDt { get; set; }
    public bool Hardware { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public string? Notes { get; set; }
    public string? InterfaceSha { get; set; }
    public int Meshes { get; set; }
    public Dictionary<string, long> Files { get; set; } = [];
}

// ─── Responses assembled by the API ─────────────────────────────────────────

public sealed class GlobalSummaryDto
{
    public DateTimeOffset? Generated { get; set; }
    public string? Stamp { get; set; }
    public bool UsedGh { get; set; }
    public int RepoCount { get; set; }
    public int RlCount { get; set; }
    public int BrainCount { get; set; }
    public int ShippedBrainCount { get; set; }
    public int ModelCount { get; set; }
    public int CiFailing { get; set; }
    public int DirtyRepos { get; set; }
    public long DiskBytes { get; set; }
    public long ReclaimableBytes { get; set; }
    public int LessonsCount { get; set; }
    public int CreaturesCount { get; set; }
    public int JobsReady { get; set; }
    public int JobsTotal { get; set; }
    public int ShippedBrainsRl { get; set; }
    public int PeakSps { get; set; }
    public string? RlRecordedAt { get; set; }
    public DateTimeOffset? UniteGenerated { get; set; }
    public int MediaCount { get; set; }
}

public sealed class GlobalVaultDto
{
    public int BrainCount { get; set; }
    public int GoldCount { get; set; }
    public int ModelCount { get; set; }
    public List<GlobalSocketDto> Sockets { get; set; } = [];
    public List<GlobalVaultBrainDto> Brains { get; set; } = [];
    public List<GlobalVaultModelDto> Models { get; set; } = [];
}

public sealed class GlobalSocketDto
{
    public string Socket { get; set; } = "";
    public int Obs { get; set; }
    public int Act { get; set; }
    public int Count { get; set; }
}

public sealed class GlobalVaultBrainDto
{
    public string Repo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rel { get; set; } = "";
    public double Kb { get; set; }
    public bool Shipped { get; set; }
    public bool Gold { get; set; }
    public int? Obs { get; set; }
    public int? Act { get; set; }
    public string Socket { get; set; } = "";
    public DateTimeOffset? Modified { get; set; }
}

public sealed class GlobalVaultModelDto
{
    public string Repo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rel { get; set; } = "";
    public string? Model { get; set; }
    public int? Bodies { get; set; }
    public int? Joints { get; set; }
    public int? Actuators { get; set; }
    public string? Timestep { get; set; }
}

public sealed class GlobalReadinessDto
{
    public List<GlobalReadinessAppDto> Apps { get; set; } = [];
}

public sealed class GlobalReadinessAppDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsRl { get; set; }
    public string Stage { get; set; } = "";
    public string Suggested { get; set; } = "";
    public string Why { get; set; } = "";
    public string Effective { get; set; } = "";
    public bool StageIsSet { get; set; }
    public string? Notes { get; set; }
    public string? LiveUrl { get; set; }
    public string? Ci { get; set; }
    public bool HasTests { get; set; }
    public double? DaysSinceCommit { get; set; }
    public int Dirty { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public List<GlobalChecklistItemDto> Checklist { get; set; } = [];
}

public sealed class GlobalChecklistItemDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class GlobalBriefingDto
{
    public DateTimeOffset? Generated { get; set; }
    public DateTimeOffset? Since { get; set; }
    public bool IsBaseline { get; set; }
    public int Commits { get; set; }
    public int NewBrains { get; set; }
    public long MovedBytes { get; set; }
    public List<string> CiFailing { get; set; } = [];
    public List<GlobalBriefingItemDto> Items { get; set; } = [];
    public List<GlobalBriefingRepoDto> Repos { get; set; } = [];
}

public sealed class GlobalBriefingItemDto
{
    public string Kind { get; set; } = "";   // commits | brains | disk | ci | dirty
    public string Repo { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class GlobalBriefingRepoDto
{
    public string Repo { get; set; } = "";
    public int Commits { get; set; }
    public int NewBrains { get; set; }
    public long DiskDelta { get; set; }
    public string? CiBefore { get; set; }
    public string? CiAfter { get; set; }
    public int DirtyBefore { get; set; }
    public int DirtyAfter { get; set; }
    public List<string> LatestCommitMessages { get; set; } = [];
}

public sealed class GlobalRlDto
{
    public List<GlobalRlRepoViewDto> Repos { get; set; } = [];
    public List<GlobalRlTimelineEntryDto> History { get; set; } = [];
    public int ShippedBrains { get; set; }
    public int AllBrains { get; set; }
    public int PeakSps { get; set; }
    public int AvgSps { get; set; }
    public int EstHours { get; set; }
    public double EstStepsB { get; set; }
    public int Updated7d { get; set; }
    public string? RecordedAt { get; set; }
}

public sealed class GlobalRlRepoViewDto
{
    public string Name { get; set; } = "";
    public int Shipped { get; set; }
    public int Found { get; set; }
    public int Mjcf { get; set; }
    public int RewardConfigs { get; set; }
    public int YamlConfigs { get; set; }
    public int Visuals { get; set; }
    public GlobalBenchmarkDto? Benchmark { get; set; }
    public string? Stack { get; set; }
    public bool InFleet { get; set; }
}

// ─── Hub (the Central Station home page) ─────────────────────────────────────

/// <summary>
/// <c>/api/global/home</c>: everything the old <c>GLOBAL/index.html</c> showed beyond the summary
/// counts — freshness, the "needs attention" list, the latest benchmark attempt, the change since
/// the previous crawl, the trend over time, and the RL data-quality flags and benchmark context.
/// </summary>
public sealed class GlobalHomeDto
{
    public DateTimeOffset? FleetGenerated { get; set; }
    public DateTimeOffset? RlRecorded { get; set; }
    public List<GlobalAttentionDto> Attention { get; set; } = [];
    public GlobalBenchmarkRunDto? Benchmark { get; set; }
    public GlobalTrendPointDto Now { get; set; } = new();
    public GlobalTrendPointDto? Previous { get; set; }
    public List<GlobalTrendPointDto> Trend { get; set; } = [];
    public List<GlobalRlFlagDto> Flags { get; set; } = [];
    public List<GlobalRlRepoViewDto> Benchmarks { get; set; } = [];
    public int JobsReady { get; set; }
    public int JobsTotal { get; set; }
}

public sealed class GlobalAttentionDto
{
    /// <summary><c>fix</c> (broken now), <c>check</c>, or <c>info</c>.</summary>
    public string Severity { get; set; } = "info";
    public string Text { get; set; } = "";
    public string Href { get; set; } = "/global";
}

/// <summary>Fleet totals at one crawl (one line of <c>history/station_timeline.jsonl</c>).</summary>
public sealed class GlobalTrendPointDto
{
    public DateTimeOffset? When { get; set; }
    public int Brains { get; set; }
    public long DiskBytes { get; set; }
    public int Dirty { get; set; }
    public int CiFailing { get; set; }
}

public sealed class GlobalRlFlagDto
{
    public string Repo { get; set; } = "";
    public string Why { get; set; } = "";
}

/// <summary><c>benchmark_last.json</c>: the newest benchmark attempt from <c>run_benchmarks.ps1</c>.</summary>
public sealed class GlobalBenchmarkRunDto
{
    public string Stamp { get; set; } = "";
    public List<GlobalBenchmarkResultDto> Results { get; set; } = [];
}

public sealed class GlobalBenchmarkResultDto
{
    public string Repo { get; set; } = "";
    public string Result { get; set; } = "";
    public int Seconds { get; set; }
    public string? Detail { get; set; }
}

