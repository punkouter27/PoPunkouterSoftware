using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Is each catalog app's source repo public, and is its Google Play listing live? Asked the
/// way a visitor would — an anonymous GET of github.com/owner/repo and of the Play Store
/// details page, through the keyless <c>public-api</c> client, never the GitHub PAT, which
/// can see private repos and would vouch for links a visitor cannot open. The home page
/// shows a link only once the answer is yes, so a game appears on Google Play here the
/// night after its listing is published, with no edit anywhere (owner's ask, 2026-11-03).
/// </summary>
public partial class AzureReportService
{
    private async Task<List<PublicLinkCheck>> CheckPublicLinksAsync(CancellationToken ct)
    {
        var apps = await LoadCatalogAsync(ct);
        var http = _httpClientFactory.CreateClient(PublicApiClientName);
        var checks = await BoundedParallelAsync(
            apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)
                            && (!string.IsNullOrWhiteSpace(a.GithubRepo) || !string.IsNullOrWhiteSpace(a.AndroidPackage))),
            maxConcurrency: 4,
            async app => new PublicLinkCheck
            {
                App = app.Name!,
                Repo = app.GithubRepo,
                RepoPublic = string.IsNullOrWhiteSpace(app.GithubRepo) ? null
                    : await AnswersOkAsync(http, $"https://github.com/{app.GithubRepo}", ct),
                AndroidPackage = app.AndroidPackage,
                PlayStoreLive = string.IsNullOrWhiteSpace(app.AndroidPackage) ? null
                    : await AnswersOkAsync(http, $"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(app.AndroidPackage)}", ct),
            }, ct);
        return checks.OrderBy(c => c.App, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True on 2xx, false on 404/410, null on anything else (rate limit, timeout) —
    /// a check that could not be made claims nothing.</summary>
    private async Task<bool?> AnswersOkAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (resp.IsSuccessStatusCode) return true;
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone) return false;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Public link check failed for {Url}: {Error}", url, ex.GetType().Name);
            return null;
        }
    }

    private async Task<List<AppMeta>> LoadCatalogAsync(CancellationToken ct)
    {
        var path = Path.Combine(ReportFileCache.GetCatalogDir(_env), "apps.json");
        if (!File.Exists(path))
            return [];
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<AppsFile>(await File.ReadAllTextAsync(path, ct),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return file?.Apps ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "apps.json could not be read for public link checks — skipping this step");
            return [];
        }
    }
}
