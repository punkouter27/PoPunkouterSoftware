using System.Text.Json.Serialization;
using FluentValidation;

namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Environment/config carrier returned by <c>/api/config</c>.
/// </summary>
public sealed record ConfigResponse(
    [property: JsonPropertyName("apiBase")] string ApiBase,
    [property: JsonPropertyName("isMockMode")] bool IsMockMode,
    [property: JsonPropertyName("managementActionsEnabled")] bool ManagementActionsEnabled);

/// <summary>
/// Minimal RFC 7807 ProblemDetails shape used to surface structured error messages.
/// </summary>
public sealed record ProblemResponse(
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("status")] int? Status = null);

/// <summary>
/// Request body for the ad-hoc <c>POST /api/diag/ai</c> rewrite endpoint.
/// </summary>
public sealed record AiTriageAdHocRequest(
    [property: JsonPropertyName("attentionItems")] IReadOnlyList<string> AttentionItems);

/// <summary>
/// Response payload from <c>POST /api/diag/ai</c>.
/// </summary>
public sealed record AiTriageAdHocResponse(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("summary")] string? Summary);

/// <summary>
/// Validated sign-in roster search query model.
/// </summary>
public sealed class SignInSearchFilter
{
    public string Query { get; set; } = string.Empty;
}

/// <summary>
/// Validates roster filter strings in both WASM UI and shared verification.
/// </summary>
public sealed class SignInSearchFilterValidator : AbstractValidator<SignInSearchFilter>
{
    public SignInSearchFilterValidator()
    {
        RuleFor(x => x.Query)
            .MaximumLength(64)
            .WithMessage("Filter must be 64 characters or fewer.")
            .Must(q => q is null || (!q.Contains('<') && !q.Contains('>')))
            .WithMessage("Filter cannot contain '<' or '>' characters.");
    }
}

/// <summary>
/// Rules for the ad-hoc <c>POST /api/diag/ai</c> rewrite. The same validator runs in the WASM
/// client (before the request leaves, so the error shows inline beside the Rewrite button) and
/// on the endpoint (which answers <c>Results.ValidationProblem</c>). The ceilings are a cost
/// guard as much as a shape check: the endpoint is anonymous and every accepted call is a paid
/// completion, so an oversized list would buy an oversized prompt.
/// </summary>
public sealed class AiTriageAdHocRequestValidator : AbstractValidator<AiTriageAdHocRequest>
{
    public const int MaxItems = 50;
    public const int MaxItemLength = 500;

    public AiTriageAdHocRequestValidator()
    {
        RuleFor(x => x.AttentionItems)
            .NotNull()
            .WithMessage("There is no attention list to rewrite yet.")
            .Must(items => items is null || items.Count <= MaxItems)
            .WithMessage($"Too many attention items to rewrite (at most {MaxItems}).");
        RuleForEach(x => x.AttentionItems)
            .NotEmpty()
            .WithMessage("An attention item is blank.")
            .MaximumLength(MaxItemLength)
            .WithMessage($"An attention item is longer than {MaxItemLength} characters.");
    }
}

/// <summary>
/// <c>GET /api/diag/status</c> — the anonymous, Production-safe source of the <c>/diag</c>
/// page. Built only from health-check verdict WORDS, latencies and fixed descriptions: never a
/// check's data dictionary (it carries exception messages, counts and timestamps), never the
/// masked config block, never a URI, path, connection string or identifier.
/// </summary>
public sealed record DiagStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("checkedAtUtc")] DateTimeOffset CheckedAtUtc,
    [property: JsonPropertyName("totalMs")] double TotalMs,
    [property: JsonPropertyName("checks")] IReadOnlyList<DiagCheck> Checks);

/// <summary>One dependency on the <c>/diag</c> page.</summary>
/// <param name="Category"><c>storage</c> (Database / storage section) or <c>service</c>.</param>
/// <param name="Status"><c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="Verdict">The check's own one-word verdict (e.g. <c>readable</c>, <c>not-configured</c>).</param>
/// <param name="Summary">The verdict in plain English.</param>
/// <param name="Purpose">What the app uses this dependency for.</param>
/// <param name="LatencyMs">How long the check took; null when nothing was probed.</param>
public sealed record DiagCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("latencyMs")] double? LatencyMs);
