using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// "Up" means more than a status code. The probe in <c>.Discovery</c> reads headers only, so a
/// blank SPA shell, a framework error page or a parked placeholder served with a 200 counts
/// as healthy. Each catalog entry may name an <c>expect</c> string in apps.json; this step
/// fetches the page body and checks the text is there.
/// </summary>
public partial class AzureReportService
{
    /// <summary>Name of the plain HTTP client for public, unauthenticated APIs and pages.</summary>
    public const string PublicApiClientName = "public-api";

    /// <summary>Bodies beyond this are not read: the expectation is a title, near the top.</summary>
    private const int ContentCheckMaxBytes = 512 * 1024;

    private async Task<List<RawService>> CheckContentAsync(List<RawService> services, CancellationToken ct)
    {
        var expectations = await LoadExpectationsAsync(ct);
        if (expectations.Count == 0)
            return services;

        var http = _httpClientFactory.CreateClient(PublicApiClientName);
        var checkedServices = await BoundedParallelAsync(services, maxConcurrency: 6, async svc =>
        {
            if (AppScreenshotService.HostOf(svc.Url ?? "") is not { } host
                || !expectations.TryGetValue(host, out var expect))
                return svc;
            return svc with { ContentCheck = await CheckPageAsync(http, svc.Url!, expect, ct) };
        }, ct);
        return checkedServices.ToList();
    }

    /// <summary>
    /// Only a page that ANSWERED 2xx without the text is a failed check. A timeout or an error
    /// status sets <see cref="ContentCheckInfo.Error"/> and claims nothing: the reachability
    /// probe already reports an app that is down, and an F1 cold start is not a content fault.
    /// </summary>
    private static async Task<ContentCheckInfo> CheckPageAsync(HttpClient http, string url, string expect, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!resp.IsSuccessStatusCode)
                return new ContentCheckInfo { Expect = expect, Error = $"HTTP {(int)resp.StatusCode}" };

            await using var stream = await resp.Content.ReadAsStreamAsync(budget.Token);
            var buffer = new byte[ContentCheckMaxBytes];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), budget.Token)) > 0)
                read += n;
            var body = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            return new ContentCheckInfo { Expect = expect, Found = body.Contains(expect, StringComparison.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ContentCheckInfo { Expect = expect, Error = ex is OperationCanceledException ? "timeout" : ex.GetType().Name };
        }
    }

    /// <summary>host → expected text, from apps.json entries that set <c>expect</c>.</summary>
    private async Task<Dictionary<string, string>> LoadExpectationsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(ReportFileCache.GetCatalogDir(_env), "apps.json");
        if (!File.Exists(path))
            return result;
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<AppsFile>(await File.ReadAllTextAsync(path, ct),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var app in file?.Apps ?? [])
            {
                if (!string.IsNullOrWhiteSpace(app.Expect) && AppScreenshotService.HostOf(app.Url ?? "") is { } host)
                    result[host] = app.Expect;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "apps.json could not be read for content checks — skipping this step");
        }
        return result;
    }
}
