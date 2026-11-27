using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Source-generated serializer context for the GLOBAL snapshot the crawler writes and the
/// API serves. Used by the host (reading <c>App_Data/global/*.json</c>) and by the WASM
/// client (reading <c>/api/global/*</c>), so neither side depends on runtime reflection —
/// the Release build is trimmed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GlobalSnapshotDto))]
[JsonSerializable(typeof(GlobalFleetDto))]
[JsonSerializable(typeof(GlobalRepoDto))]
[JsonSerializable(typeof(GlobalLessonsFileDto))]
[JsonSerializable(typeof(GlobalReadinessFileDto))]
[JsonSerializable(typeof(GlobalVaultGoldFileDto))]
[JsonSerializable(typeof(GlobalTrainJobsFileDto))]
[JsonSerializable(typeof(GlobalQueueDto))]
[JsonSerializable(typeof(GlobalStandardsDto))]
[JsonSerializable(typeof(LocalHelperRequest))]
[JsonSerializable(typeof(LocalHelperResult))]
[JsonSerializable(typeof(Dictionary<string, GlobalRlRepoFactDto>))]
[JsonSerializable(typeof(GlobalTimelineEntryDto))]
[JsonSerializable(typeof(GlobalRlTimelineEntryDto))]
[JsonSerializable(typeof(GlobalUniteDto))]
[JsonSerializable(typeof(GlobalSummaryDto))]
[JsonSerializable(typeof(GlobalVaultDto))]
[JsonSerializable(typeof(GlobalReadinessDto))]
[JsonSerializable(typeof(GlobalBriefingDto))]
[JsonSerializable(typeof(GlobalRlDto))]
public sealed partial class GlobalJsonContext : JsonSerializerContext;
