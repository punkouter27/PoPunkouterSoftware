using System.Text.Json;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Extracts human-readable RFC 7807 <c>detail</c> or <c>title</c> text from failed API responses
/// using the AOT/trim-safe <see cref="AppJsonContext"/>.
/// </summary>
internal static class ProblemDetailsParser
{
    public static async Task<string?> TryReadDetailAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var problem = JsonSerializer.Deserialize(body, AppJsonContext.Default.ProblemResponse);
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            return string.IsNullOrWhiteSpace(problem?.Title) ? null : problem.Title;
        }
        catch
        {
            return null;
        }
    }
}
