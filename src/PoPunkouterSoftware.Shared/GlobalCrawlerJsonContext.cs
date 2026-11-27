using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Reads the crawler's files under <c>App_Data/global</c>. The Python crawlers write
/// <c>snake_case</c> keys (<c>is_git</c>, <c>dirty_count</c>, <c>onnx_shas</c>), which the
/// API's <c>camelCase</c> wire contract — <see cref="GlobalJsonContext"/> — does not match.
/// Two contexts over the same types keep the wire camelCase and the files readable without a
/// hand-written mapper for every property.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GlobalSnapshotDto))]
[JsonSerializable(typeof(GlobalLessonsFileDto))]
[JsonSerializable(typeof(GlobalReadinessFileDto))]
[JsonSerializable(typeof(GlobalVaultGoldFileDto))]
[JsonSerializable(typeof(GlobalTrainJobsFileDto))]
[JsonSerializable(typeof(GlobalQueueDto))]
[JsonSerializable(typeof(GlobalBenchmarkRunDto))]
[JsonSerializable(typeof(GlobalStandardsDto))]
[JsonSerializable(typeof(Dictionary<string, GlobalRlRepoFactDto>))]
[JsonSerializable(typeof(GlobalTimelineEntryDto))]
[JsonSerializable(typeof(GlobalRlTimelineEntryDto))]
public sealed partial class GlobalCrawlerJsonContext : JsonSerializerContext;
