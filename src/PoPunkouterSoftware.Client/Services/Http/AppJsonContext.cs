using System.Text.Json.Serialization;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every type the WASM client
/// (de)serialises. Shared DTOs live in <see cref="PoPunkouterSoftware.Shared"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzureReport))]
[JsonSerializable(typeof(OpsSummary))]
[JsonSerializable(typeof(AiSummaryResult))]
[JsonSerializable(typeof(PortfolioResponse))]
[JsonSerializable(typeof(SignInReport))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(AiTriageAdHocRequest))]
[JsonSerializable(typeof(AiTriageAdHocResponse))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
