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
