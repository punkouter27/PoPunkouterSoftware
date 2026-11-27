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
    public DateTimeOffset? Generated { get; init; }
    public string? Stamp { get; init; }
    public bool UsedGh { get; init; }
    public List<GlobalRepoDto> Repos { get; init; } = [];
}

public sealed class GlobalRepoDto
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool IsRl { get; init; }
    public string? RlStack { get; init; }
    public string? UnityVersion { get; init; }
    public GlobalGitDto Git { get; init; } = new();
    public GlobalCiDto Ci { get; init; } = new();
    public GlobalDocsDto Docs { get; init; } = new();
    public GlobalMediaDto Media { get; init; } = new();
    public List<GlobalBrainDto> Onnx { get; init; } = [];
    public List<GlobalModelDto> Mjcf { get; init; } = [];
    public GlobalDiskDto Disk { get; init; } = new();
}

public sealed class GlobalGitDto
{
    public bool IsGit { get; init; }
    public string? Branch { get; init; }
    public int DirtyCount { get; init; }
    public List<string> DirtyFiles { get; init; } = [];
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public DateTimeOffset? LastCommitIso { get; init; }
    public string? LastCommitMsg { get; init; }
    public string? LastCommitHash { get; init; }
    public double? DaysSinceCommit { get; init; }
    public int Commits7d { get; init; }
    public int Commits30d { get; init; }
    public int CommitsTotal { get; init; }
    public string? Remote { get; init; }
    public string? Slug { get; init; }
    public List<GlobalCommitDto> RecentCommits { get; init; } = [];
}

public sealed class GlobalCommitDto
{
    public DateTimeOffset? Iso { get; init; }
    public string? Hash { get; init; }
    public string? Msg { get; init; }
}

public sealed class GlobalCiDto
{
    public List<string> Workflows { get; init; } = [];
    public GlobalCiRunDto? LatestRun { get; init; }
}

public sealed class GlobalCiRunDto
{
    public string? Conclusion { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? Event { get; init; }
    public string? HeadBranch { get; init; }
    public string? Status { get; init; }
    public string? Url { get; init; }
    public string? WorkflowName { get; init; }
    public List<string> Recent { get; init; } = [];
}

public sealed class GlobalDocsDto
{
    public Dictionary<string, bool> Files { get; init; } = [];
    public List<string> Tests { get; init; } = [];
    public string? LiveUrl { get; init; }
    public bool BenchmarkPs1 { get; init; }
    public bool BenchmarkHistory { get; init; }
}

public sealed class GlobalMediaDto
{
    public int Count { get; init; }
    public int GifCount { get; init; }
    public List<GlobalMediaItemDto> Newest { get; init; } = [];
    public List<GlobalMediaItemDto> Showcase { get; init; } = [];
}

public sealed class GlobalMediaItemDto
{
    public string Rel { get; init; } = "";
    public string? Ext { get; init; }
    public double Kb { get; init; }
    public long Mtime { get; init; }
}

public sealed class GlobalBrainDto
{
    public string Rel { get; init; } = "";
    public string Name { get; init; } = "";
    public double Kb { get; init; }
    public long Mtime { get; init; }
    public string? Sha1 { get; init; }
    public bool Shipped { get; init; }
    public int? Opset { get; init; }
    public int? Obs { get; init; }
    public int? Act { get; init; }
}

public sealed class GlobalModelDto
{
    public string Rel { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Model { get; init; }
    public int? Bodies { get; init; }
    public int? Joints { get; init; }
    public int? FreeJoints { get; init; }
    public int? Actuators { get; init; }
    public string? Timestep { get; init; }
    public int? Includes { get; init; }
    public long Mtime { get; init; }
}

public sealed class GlobalDiskDto
{
    public long? TotalBytes { get; init; }
    public Dictionary<string, long> Categories { get; init; } = [];
    public List<GlobalHeavyDirDto> HeavyDirs { get; init; } = [];
    public int ScanErrors { get; init; }
    public bool Complete { get; init; }
    public bool Stale { get; init; }
}

public sealed class GlobalHeavyDirDto
{
    public string Rel { get; init; } = "";
    public string? Category { get; init; }
    public string? Safety { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
}

// ─── Station readings ───────────────────────────────────────────────────────

public sealed class GlobalLessonsFileDto
{
    public int Count { get; init; }
    public List<GlobalLessonDto> Lessons { get; init; } = [];
}

public sealed class GlobalLessonDto
{
    public string? Id { get; init; }
    public string Repo { get; init; } = "";
    public List<string> Repos { get; init; } = [];
    public string? File { get; init; }
    public int Line { get; init; }
    public string? Heading { get; init; }
    public string Kind { get; init; } = "trap";
    public string Text { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public bool Manual { get; init; }
}

public sealed class GlobalReadinessFileDto
{
    public Dictionary<string, GlobalReadinessEntryDto> Apps { get; init; } = [];
}

public sealed class GlobalReadinessEntryDto
{
    public string? Stage { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, bool> Checklist { get; init; } = [];
}

public sealed class GlobalVaultGoldFileDto
{
    public Dictionary<string, bool> Gold { get; init; } = [];
}

public sealed class GlobalTrainJobsFileDto
{
    public List<GlobalTrainJobDto> Jobs { get; init; } = [];
}

public sealed class GlobalTrainJobDto
{
    public string? Id { get; init; }
    public string? Repo { get; init; }
    public string? Label { get; init; }
    public string? Cwd { get; init; }
    public string? Device { get; init; }
    public bool SelfLimiting { get; init; }
    public List<string> Requires { get; init; } = [];
}

/// <summary>One line of <c>history/station_timeline.jsonl</c>.</summary>
public sealed class GlobalTimelineEntryDto
{
    public DateTimeOffset? When { get; init; }
    public string? Stamp { get; init; }
    public Dictionary<string, GlobalTimelineRepoDto> Repos { get; init; } = [];
}

public sealed class GlobalTimelineRepoDto
{
    public long? Bytes { get; init; }
    public Dictionary<string, long> Cats { get; init; } = [];
    public int? Dirty { get; init; }
    public int? CommitsTotal { get; init; }
    public string? LastHash { get; init; }
    public int Onnx { get; init; }
    public List<string> OnnxShas { get; init; } = [];
    public int Media { get; init; }
    public string? Ci { get; init; }
    public bool DiskStale { get; init; }
}

public sealed class GlobalRlRepoFactDto
{
    public GlobalBenchmarkDto? Benchmark { get; init; }
    public int OnnxTotalFound { get; init; }
    public int OnnxShippedCount { get; init; }
    public int MjcfCount { get; init; }
    public List<string> YamlConfigs { get; init; } = [];
    public List<string> RewardConfigs { get; init; } = [];
    public List<GlobalMediaItemDto> Visuals { get; init; } = [];
}

public sealed class GlobalBenchmarkDto
{
    public DateTimeOffset? When { get; init; }
    public bool Ok { get; init; }
    public string? MachineGpu { get; init; }
    public string? Trainer { get; init; }
    public string? Script { get; init; }
    public int? NumEnvs { get; init; }
    public int? Horizon { get; init; }
    public List<int> Net { get; init; } = [];
    public double? StepTimeMs { get; init; }
    public double? EnvStepsPerS { get; init; }
    public Dictionary<string, string> Versions { get; init; } = [];
    public int? TotalBenchmarkRuns { get; init; }
}

public sealed class GlobalRlTimelineEntryDto
{
    public string? Timestamp { get; init; }
    public int TotalShippedOnnx { get; init; }
    public int TotalAllOnnx { get; init; }
    public int Updated7dOnnx { get; init; }
    public int PeakSps { get; init; }
    public int AvgSps { get; init; }
    public int EstTotalHours { get; init; }
    public double EstTotalStepsB { get; init; }
    public Dictionary<string, int> PerRepoOnnx { get; init; } = [];
    public Dictionary<string, int> PerRepoSps { get; init; } = [];
}

// ─── UNITE ──────────────────────────────────────────────────────────────────

public sealed class GlobalUniteDto
{
    public DateTimeOffset? Generated { get; init; }
    public GlobalUniteRulesDto Rules { get; init; } = new();
    public List<GlobalUniteDocumentDto> Documents { get; init; } = [];
    public List<GlobalCreatureDto> Creatures { get; init; } = [];
}

public sealed class GlobalUniteRulesDto
{
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public string Markdown { get; init; } = "";
}

public sealed class GlobalUniteDocumentDto
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long Bytes { get; init; }
}

public sealed class GlobalCreatureDto
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public int? MorphologyId { get; init; }
    public int Joints { get; init; }
    public int RolesTotal { get; init; }
    public int RolesMapped { get; init; }
    public List<string> HeldOutside { get; init; } = [];
    public int? Obs { get; init; }
    public int? Action { get; init; }
    public double? Timestep { get; init; }
    public double? ControlDt { get; init; }
    public bool Hardware { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? GeneratedAt { get; init; }
    public string? Notes { get; init; }
    public string? InterfaceSha { get; init; }
    public int Meshes { get; init; }
    public Dictionary<string, long> Files { get; init; } = [];
}

// ─── Responses assembled by the API ─────────────────────────────────────────

public sealed class GlobalSummaryDto
{
    public DateTimeOffset? Generated { get; init; }
    public string? Stamp { get; init; }
    public bool UsedGh { get; init; }
    public int RepoCount { get; init; }
    public int RlCount { get; init; }
    public int BrainCount { get; init; }
    public int ShippedBrainCount { get; init; }
    public int ModelCount { get; init; }
    public int CiFailing { get; init; }
    public int DirtyRepos { get; init; }
    public long DiskBytes { get; init; }
    public long ReclaimableBytes { get; init; }
    public int LessonsCount { get; init; }
    public int CreaturesCount { get; init; }
    public int JobsReady { get; init; }
    public int JobsTotal { get; init; }
    public int ShippedBrainsRl { get; init; }
    public int PeakSps { get; init; }
    public string? RlRecordedAt { get; init; }
    public DateTimeOffset? UniteGenerated { get; init; }
    public int MediaCount { get; init; }
}

public sealed class GlobalVaultDto
{
    public int BrainCount { get; init; }
    public int GoldCount { get; init; }
    public int ModelCount { get; init; }
    public List<GlobalSocketDto> Sockets { get; init; } = [];
    public List<GlobalVaultBrainDto> Brains { get; init; } = [];
    public List<GlobalVaultModelDto> Models { get; init; } = [];
}

public sealed class GlobalSocketDto
{
    public string Socket { get; init; } = "";
    public int Obs { get; init; }
    public int Act { get; init; }
    public int Count { get; init; }
}

public sealed class GlobalVaultBrainDto
{
    public string Repo { get; init; } = "";
    public string Name { get; init; } = "";
    public string Rel { get; init; } = "";
    public double Kb { get; init; }
    public bool Shipped { get; init; }
    public bool Gold { get; init; }
    public int? Obs { get; init; }
    public int? Act { get; init; }
    public string Socket { get; init; } = "";
    public DateTimeOffset? Modified { get; init; }
}

public sealed class GlobalVaultModelDto
{
    public string Repo { get; init; } = "";
    public string Name { get; init; } = "";
    public string Rel { get; init; } = "";
    public string? Model { get; init; }
    public int? Bodies { get; init; }
    public int? Joints { get; init; }
    public int? Actuators { get; init; }
    public string? Timestep { get; init; }
}

public sealed class GlobalReadinessDto
{
    public List<GlobalReadinessAppDto> Apps { get; init; } = [];
}

public sealed class GlobalReadinessAppDto
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool IsRl { get; init; }
    public string Stage { get; init; } = "";
    public string Suggested { get; init; } = "";
    public string Why { get; init; } = "";
    public string Effective { get; init; } = "";
    public bool StageIsSet { get; init; }
    public string? Notes { get; init; }
    public string? LiveUrl { get; init; }
    public string? Ci { get; init; }
    public bool HasTests { get; init; }
    public double? DaysSinceCommit { get; init; }
    public int Dirty { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public List<GlobalChecklistItemDto> Checklist { get; init; } = [];
}

public sealed class GlobalChecklistItemDto
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Done { get; init; }
}

public sealed class GlobalBriefingDto
{
    public DateTimeOffset? Generated { get; init; }
    public DateTimeOffset? Since { get; init; }
    public bool IsBaseline { get; init; }
    public int Commits { get; init; }
    public int NewBrains { get; init; }
    public long MovedBytes { get; init; }
    public List<string> CiFailing { get; init; } = [];
    public List<GlobalBriefingItemDto> Items { get; init; } = [];
    public List<GlobalBriefingRepoDto> Repos { get; init; } = [];
}

public sealed class GlobalBriefingItemDto
{
    public string Kind { get; init; } = "";   // commits | brains | disk | ci | dirty
    public string Repo { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Detail { get; init; }
}

public sealed class GlobalBriefingRepoDto
{
    public string Repo { get; init; } = "";
    public int Commits { get; init; }
    public int NewBrains { get; init; }
    public long DiskDelta { get; init; }
    public string? CiBefore { get; init; }
    public string? CiAfter { get; init; }
    public int DirtyBefore { get; init; }
    public int DirtyAfter { get; init; }
    public List<string> LatestCommitMessages { get; init; } = [];
}

public sealed class GlobalRlDto
{
    public List<GlobalRlRepoViewDto> Repos { get; init; } = [];
    public List<GlobalRlTimelineEntryDto> History { get; init; } = [];
    public int ShippedBrains { get; init; }
    public int AllBrains { get; init; }
    public int PeakSps { get; init; }
    public int AvgSps { get; init; }
    public int EstHours { get; init; }
    public double EstStepsB { get; init; }
    public int Updated7d { get; init; }
    public string? RecordedAt { get; init; }
}

public sealed class GlobalRlRepoViewDto
{
    public string Name { get; init; } = "";
    public int Shipped { get; init; }
    public int Found { get; init; }
    public int Mjcf { get; init; }
    public int RewardConfigs { get; init; }
    public int YamlConfigs { get; init; }
    public int Visuals { get; init; }
    public GlobalBenchmarkDto? Benchmark { get; init; }
    public string? Stack { get; init; }
    public bool InFleet { get; init; }
}
