using Azure.Core;
using Azure.Monitor.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Exceptions per app over the last 24 hours, from the shared Application Insights component
/// every Po app exports to (<c>SignIns:ResourceId</c> — the same component the /users roster
/// reads). This is the "it answers HEAD but it is throwing" signal a reachability probe cannot
/// see. Read once per scan and stored, so the uptime grid shows it without a live query.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<AppErrorSummary>> GetAppErrorsAsync(CancellationToken ct)
    {
        var resourceId = _config["SignIns:ResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
            return [];

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            var response = await new LogsQueryClient(_credential).QueryResourceAsync(
                new ResourceIdentifier(resourceId),
                """
                exceptions
                | summarize N = count(), First = min(timestamp), Last = max(timestamp) by App = cloud_RoleName, Type = type
                | take 500
                """,
                new QueryTimeRange(TimeSpan.FromHours(24)),
                cancellationToken: budget.Token);

            return SummarizeAppErrors(response.Value.Table.Rows.Select(r =>
                (r.GetString("App") ?? "", r.GetString("Type") ?? "", (int)(r.GetInt64("N") ?? 0),
                    r.GetDateTimeOffset("First")?.UtcDateTime ?? default, r.GetDateTimeOffset("Last")?.UtcDateTime ?? default)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No role, no credential, or a slow workspace: the grid simply shows no error chips.
            _logger.LogWarning(ex, "App exception query failed against {ResourceId}", resourceId);
            return [];
        }
    }

    /// <summary>Pure: (app, type, count, first, last) rows to one summary per app, types by count, top ten kept.</summary>
    public static List<AppErrorSummary> SummarizeAppErrors(
        IEnumerable<(string App, string Type, int Count, DateTime First, DateTime Last)> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.App) && r.Count > 0)
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppErrorSummary
            {
                App = g.Key,
                Count24h = g.Sum(r => r.Count),
                TopType = g.OrderByDescending(r => r.Count).First().Type is { Length: > 0 } t ? t : null,
                Types = g.Where(r => r.Type.Length > 0)
                    .OrderByDescending(r => r.Count)
                    .Take(10)
                    .Select(r => new AppErrorType { Type = r.Type, Count = r.Count, FirstSeen = r.First, LastSeen = r.Last })
                    .ToList(),
            })
            .OrderByDescending(s => s.Count24h)
            .ToList();
}
