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
/// Cross-step helpers: run-over-run delta, and the name canonicalisation used to match Azure resource names to friendly names.
/// </summary>
public partial class AzureReportService
{
    private static ReportDelta? ComputeDelta(AzureReport current, AzureReport? previous)
    {
        if (previous is null)
            return null;

        var currentBroken = current.WebServices?.Services
            .Where(s => s.HttpStatus == "broken").Select(s => s.Name).ToHashSet() ?? [];
        var previousBroken = previous.WebServices?.Services
            .Where(s => s.HttpStatus == "broken").Select(s => s.Name).ToHashSet() ?? [];

        var currentOrphaned = current.OrphanedResources?.Select(o => o.Name).ToHashSet() ?? [];
        var previousOrphaned = previous.OrphanedResources?.Select(o => o.Name).ToHashSet() ?? [];

        var costDelta = current.Cost is not null && previous.Cost is not null
            ? Math.Round(current.Cost.TotalCost30Days - previous.Cost.TotalCost30Days, 4)
            : (double?)null;

        return new ReportDelta
        {
            PreviousGeneratedAt = previous.GeneratedAt,
            BrokenServicesDelta = currentBroken.Count - previousBroken.Count,
            CostDelta = costDelta,
            NewBrokenServices = currentBroken.Except(previousBroken).ToList(),
            RecoveredServices = previousBroken.Except(currentBroken).ToList(),
            NewOrphanedResources = currentOrphaned.Except(previousOrphaned).ToList(),
        };
    }

    private static string GetCanonicalName(string name)
    {
        var r = System.Text.RegularExpressions.Regex.Replace(
            name, @"^(swa-|stapp-|wa-|app-|api-|ca-)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        r = System.Text.RegularExpressions.Regex.Replace(
            r, @"(-api|-web|-server|-app|-prod)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        r = System.Text.RegularExpressions.Regex.Replace(
            r, @"-[a-z0-9]{9,}$",
            m => m.Value.TrimStart('-') is { } seg && seg.Any(char.IsDigit) && seg.Any(char.IsLetter) ? "" : m.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return r.ToLowerInvariant();
    }

    private static string FriendlyFromContext(string rawName, string? resourceGroup)
    {
        if (resourceGroup is { Length: > 2 } rg && char.IsUpper(rg[2]) && rg != "PoShared")
            return rg;
        var canonical = GetCanonicalName(rawName);
        var parts = canonical.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var deduped = parts.Where((p, i) => i == 0 || p != parts[i - 1]).ToArray();
        var clean = System.Text.RegularExpressions.Regex.Replace(
            string.Join("-", deduped), "^po", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (string.IsNullOrEmpty(clean))
            return rawName;
        return "Po" + string.Concat(clean.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpper(p[0]) + p[1..]));
    }

    private static string ShortType(string? t)
        => t?.Split('/').LastOrDefault() ?? t ?? "Unknown";

    /// <summary>
    /// Runs <paramref name="body"/> over <paramref name="items"/> with at most
    /// <paramref name="maxConcurrency"/> in flight at once, preserving input order in the
    /// returned array regardless of completion order. Shared by the scan steps that fan out
    /// many independent per-item HTTP calls (orphan-resource checks, GitHub workflow-run
    /// lookups) instead of each hand-rolling its own SemaphoreSlim gate/try/finally.
    /// </summary>
    private static async Task<TResult[]> BoundedParallelAsync<TItem, TResult>(
        IEnumerable<TItem> items, int maxConcurrency, Func<TItem, Task<TResult>> body, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await body(item);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }
}
