using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Correlates discovered services with the latest GitHub Actions workflow run on their repo.
/// </summary>
public partial class AzureReportService
{
    /// <summary>
    /// Correlates discovered services with their GitHub repo's latest workflow run.
    ///
    /// <para>The PAT is bound once, on the "github" client's default headers, in Program.cs.
    /// This method must NOT reassign <c>DefaultRequestHeaders</c> on the instance returned by
    /// <c>CreateClient</c>: handler chains are pooled and shared between callers, so mutating
    /// an instance's defaults leaks that credential to every other consumer of the same named
    /// client for the lifetime of the handler. It previously did exactly that, and additionally
    /// shelled out to <c>gh auth token</c> mid-scan — a fallback that can never fire in App
    /// Service (no gh CLI on the worker) and that blocked the scan on process startup when it
    /// did. Both are gone: no PAT configured simply means no correlation.</para>
    /// </summary>
    private async Task<List<InfraReview>> LoadInfraReviewsForCorrelationAsync(
        List<RawService> services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config["GitHub:PersonalAccessToken"]))
            return [];

        try
        {
            return await FetchInfraReviewsAsync(_httpClientFactory.CreateClient("github"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub workflow correlation skipped");
            return [];
        }
    }

    private async Task<List<InfraReview>> FetchInfraReviewsAsync(HttpClient http, CancellationToken ct)
    {
        var reviews = new List<InfraReview>();
        var page = 1;

        while (true)
        {
            var url = $"https://api.github.com/user/repos?visibility=all&affiliation=owner&per_page=100&page={page}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var repos = doc.RootElement.EnumerateArray().ToList();
            if (repos.Count == 0)
                break;

            // Gated by BoundedParallelAsync — each repo's workflow-run lookup is independent,
            // but unbounded concurrency would hammer the GitHub API across a large repo list.
            var pageReviews = await BoundedParallelAsync(repos, maxConcurrency: 6, async repo =>
            {
                var fullName = repo.GetProperty("full_name").GetString() ?? "";
                var repoUrl = repo.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
                var defaultBranch = repo.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";

                var (runStatus, runConclusion, runCompleted, runUrl, runName, runSha) =
                    await FetchLatestWorkflowRunAsync(http, fullName, defaultBranch, ct);

                return new InfraReview
                {
                    FullName = fullName,
                    RepoName = repo.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "",
                    RepoUrl = repoUrl,
                    DefaultBranch = defaultBranch,
                    LatestWorkflowRunStatus = runStatus,
                    LatestWorkflowRunConclusion = runConclusion,
                    LatestWorkflowRunCompletedAt = runCompleted,
                    LatestWorkflowRunUrl = runUrl,
                    LatestWorkflowRunName = runName,
                    LatestWorkflowRunHeadSha = runSha,
                };
            }, ct);
            reviews.AddRange(pageReviews);

            if (repos.Count < 100)
                break;

            page++;
        }

        return reviews;
    }

    /// <summary>
    /// Latest PUSH-triggered run on the default branch. Push, because every Po repo deploys on
    /// push to its default branch, and without the filter a nightly scheduled workflow (this
    /// repo has two) would be reported as a deploy every single day.
    /// </summary>
    private async Task<(string? status, string? conclusion, DateTime? completedAt, string? runUrl, string? runName, string? headSha)>
        FetchLatestWorkflowRunAsync(HttpClient http, string fullName, string defaultBranch, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{fullName}/actions/runs?branch={Uri.EscapeDataString(defaultBranch)}&event=push&per_page=1";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null, null, null, null);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
                return (null, null, null, null, null, null);

            var run = runs[0];
            var status = run.TryGetProperty("status", out var st) ? st.GetString() : null;
            var conclusion = run.TryGetProperty("conclusion", out var conc) ? conc.GetString() : null;
            var runUrl = run.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            var runName = run.TryGetProperty("name", out var rn) ? rn.GetString() : null;
            var headSha = run.TryGetProperty("head_sha", out var hs) ? hs.GetString() : null;

            DateTime? completedAt = run.TryGetProperty("updated_at", out var ua)
                && ua.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ua.GetString(), out var dt) ? dt : null;

            return (status, conclusion, completedAt, runUrl, runName, headSha);
        }
        catch
        {
            return (null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// Open Dependabot alerts per repo, for the repos a scanned service matched — not every
    /// owned repo, which would be a hundred calls for numbers nothing shows. A repo the PAT
    /// cannot read alerts on (403: the token lacks "Dependabot alerts: read"; 404: Dependabot
    /// is off) is simply absent, never zero — zero would claim a clean bill of health.
    /// </summary>
    private async Task<Dictionary<string, int>> FetchSecurityAlertCountsAsync(
        IEnumerable<string> fullNames, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("github");
        var counts = await BoundedParallelAsync(
            fullNames.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase), maxConcurrency: 6,
            async name =>
            {
                try
                {
                    // ponytail: one page of 100 — "100+" is as actionable as an exact count.
                    using var resp = await http.GetAsync(
                        $"https://api.github.com/repos/{name}/dependabot/alerts?state=open&per_page=100", ct);
                    if (!resp.IsSuccessStatusCode)
                        return (name, (int?)null);
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    return (name, doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : (int?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Dependabot alert count failed for {Repo}", name);
                    return (name, (int?)null);
                }
            }, ct);

        return counts.Where(c => c.Item2 is not null)
            .ToDictionary(c => c.name, c => c.Item2!.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A completed run as a deploy marker; null for a run still in flight.</summary>
    private static DeployInfo? ToDeployInfo(InfraReview? review) =>
        review is { LatestWorkflowRunStatus: "completed", LatestWorkflowRunCompletedAt: DateTime at }
            ? new DeployInfo
            {
                CompletedAt = DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc),
                Conclusion = review.LatestWorkflowRunConclusion,
                Sha = review.LatestWorkflowRunHeadSha,
                Url = review.LatestWorkflowRunUrl,
            }
            : null;

    private static InfraReview? MatchServiceToInfraReview(RawService svc, List<InfraReview> reviews)
    {
        if (reviews.Count == 0)
            return null;

        // Direct match: repo name == service name
        var direct = reviews.FirstOrDefault(r =>
            r.RepoName.Equals(svc.Name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        // Fuzzy match: service name contains repo name or vice versa
        var fuzzy = reviews.FirstOrDefault(r => NameMatching.FuzzyContains(svc.Name, r.RepoName));
        if (fuzzy is not null)
            return fuzzy;

        // Match by resource group name containing repo name
        var byRg = reviews.FirstOrDefault(r =>
            !string.IsNullOrEmpty(svc.ResourceGroup) &&
            svc.ResourceGroup.Contains(r.RepoName, StringComparison.OrdinalIgnoreCase));
        if (byRg is not null)
            return byRg;

        // Match by friendly name
        return reviews.FirstOrDefault(r =>
            !string.IsNullOrEmpty(svc.FriendlyName) && NameMatching.FuzzyContains(svc.FriendlyName, r.RepoName));
    }
}
