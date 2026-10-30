using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Azure Advisor: Microsoft's own cost, security and reliability recommendations for the
/// subscription. Reader covers the read, so this needs no new role.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(
        string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return [];

        var client = _httpClientFactory.CreateClient("azure-arm");
        var list = new List<AdvisorRecommendation>();
        string? url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Advisor/recommendations?api-version=2023-01-01";
        try
        {
            // ponytail: five pages is far more than a personal subscription produces; follow
            // nextLink further if an estate ever outgrows it.
            for (var page = 0; url is not null && page < 5; page++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Advisor recommendations returned HTTP {Status}", (int)resp.StatusCode);
                    break;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                list.AddRange(ReadAdvisorPage(doc.RootElement));
                url = doc.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Advisor recommendations could not be read");
        }

        return list;
    }

    /// <summary>Pure: one Advisor list page to records. Rows without a problem statement are skipped.</summary>
    public static List<AdvisorRecommendation> ReadAdvisorPage(JsonElement root)
    {
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var result = new List<AdvisorRecommendation>();
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetProperty("properties", out var p))
                continue;
            var shortDesc = p.TryGetProperty("shortDescription", out var sd) ? sd : default;
            var problem = Str(shortDesc, "problem");
            if (string.IsNullOrWhiteSpace(problem))
                continue;

            var ext = p.TryGetProperty("extendedProperties", out var e) ? e : default;
            result.Add(new AdvisorRecommendation
            {
                Category = Str(p, "category") ?? "",
                Impact = Str(p, "impact") ?? "",
                Problem = problem,
                Solution = Str(shortDesc, "solution"),
                // A subscription-level recommendation names the subscription by its raw GUID,
                // and this list reaches the anonymous /api/diag/report — which masks ids only
                // inside /subscriptions/ paths, not a bare GUID.
                ImpactedName = Guid.TryParse(Str(p, "impactedValue"), out _) ? "subscription" : Str(p, "impactedValue") ?? "",
                ImpactedType = Str(p, "impactedField"),
                MonthlySavings = double.TryParse(Str(ext, "savingsAmount"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)
                    ? Math.Round(s, 2) : null,
            });
        }

        return result;
    }
}
