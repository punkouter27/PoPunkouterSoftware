using System.Text.Json;
using System.Text.RegularExpressions;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

/// <summary>
/// Serves a stable portfolio catalog decorated with live Azure inventory. The catalog
/// remains visible during an outage; services never disappear merely because a probe fails.
/// </summary>
internal static partial class PortfolioEndpoints
{
    internal static WebApplication MapPortfolioEndpoints(this WebApplication app)
    {
        var portfolio = app.MapGroup("/api/portfolio").WithTags("Portfolio");

        portfolio.MapGet("", GetPortfolio)
            .WithName("GetPortfolio");

        portfolio.MapGet("/screenshots/{host}", GetScreenshot)
            .WithName("GetPortfolioScreenshot");

        // Consumer: the README of each app's GitHub repo, as
        // ![status](https://app-popunkoutersoftware2.azurewebsites.net/api/portfolio/badge/<id>.svg)
        portfolio.MapGet("/badge/{id}.svg", GetBadge)
            .WithName("GetPortfolioBadge");

        return app;
    }

    internal static async Task<IResult> GetPortfolio(
        IWebHostEnvironment env, IConfiguration config, AzureReportStore store, AppScreenshotService screenshots,
        UptimeSampleStore uptimeSamples, TimeProvider clock,
        ReportRefreshRunner refreshRunner, ILogger<Program> logger, CancellationToken ct)
    {
        var inventoryTask = LoadInventoryAsync(env, store, logger, ct);
        var metadataTask = LoadMetadataAsync(env, logger, ct);
        var screenshotVersionsTask = screenshots.ListVersionsAsync(ct);
        var scoresTask = screenshots.LoadScoresAsync(ct);
        var uptimeTask = LoadUptimeAsync(store, uptimeSamples, clock, ct);
        await Task.WhenAll(inventoryTask, metadataTask, screenshotVersionsTask, scoresTask, uptimeTask);
        var (report, services) = inventoryTask.Result;
        var metadata = metadataTask.Result;
        var screenshotVersions = screenshotVersionsTask.Result;

        var stale = PortfolioFreshness.IsStale(report?.GeneratedAt, DateTime.UtcNow);

        // Self-healing inventory: production has no interactive refresh (management actions
        // are disabled there), so a stale report schedules its own background rescan. Off in
        // Development/Testing — local runs and test suites must never start real ARM scans.
        if (stale && config.GetValue("FeatureFlags:EnableAutoInventoryRefresh", !env.IsDevelopment() && !env.IsEnvironment("Testing")))
        {
            if (refreshRunner.TryStartAuto())
                logger.LogInformation("Inventory is stale — background Azure rescan started");
        }
        // The streak chip: days since each card's last bad day on the /azure uptime grid —
        // the same row, so the card can never claim a streak the grid contradicts.
        var apps = BuildApps(services, metadata, screenshotVersions, scoresTask.Result, screenshots.ScreenshotsEnabled)
            .Select(a => a with { CleanDays = RowFor(uptimeTask.Result, a)?.CleanDays })
            .ToList();

        // Any app whose screenshot is missing or over a week old: serve what is stored now
        // and capture just those in the background for the next visitor. Scan-derived
        // targets first; catalog fills the gap so a dev environment with no live Azure
        // report still produces previews. Detached from the request lifetime on purpose —
        // must not die when this response completes.
        var targets = screenshots.DueForCapture(AppScreenshotService.CombinedTargets(env, report), screenshotVersions);
        if (targets.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await screenshots.CaptureAsync(targets, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background screenshot refresh failed");
                }
            }, CancellationToken.None);
        }

        return Results.Json(new PortfolioResponse
        {
            GeneratedAt = report?.GeneratedAt,
            Stale = stale,
            RefreshInProgress = refreshRunner.IsRunning,
            // Stable for the lifetime of the server process; lets the client detect
            // "this WASM bundle was built against inventory that is older than the API".
            BuildId = (long)(System.Reflection.Assembly.GetEntryAssembly()?.Location is { } loc && File.Exists(loc)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(loc)).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Apps = apps,
        });
    }

    /// <summary>
    /// The card list: live inventory merged with the curated catalog. Shared by the portfolio
    /// and the README badge, so a badge can never disagree with the card it describes.
    /// </summary>
    private static List<PortfolioApp> BuildApps(
        List<WebService> services, List<AppMeta> metadata,
        IReadOnlyDictionary<string, long> screenshotVersions,
        IReadOnlyDictionary<string, LighthouseScores> scores, bool captureOnDemand = false)
    {
        var metaByName = metadata
            .GroupBy(m => PortfolioIdentity.NormalizeName(m.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => StatusRank(m.Status)).First(), StringComparer.OrdinalIgnoreCase);

        // One dictionary per source — services as the primary list (live inventory wins on
        // overlap), catalog filling in apps that have no live presence yet. The previous
        // shape did the same merge with two imperative loops and a `ContainsKey` guard, but
        // the precedence it implemented — Azure overwrites catalog when both exist — was
        // implicit in the loop order and easy to invert by accident. Build the live list
        // first, then add catalog entries for keys nothing else claimed.
        var appsByName = services
            .Where(s => !PortfolioIdentity.IsSelf(s.FriendlyName, s.Name))
            .ToDictionary(
                s => PortfolioIdentity.NormalizeName(string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName),
                s =>
                {
                    var key = PortfolioIdentity.NormalizeName(string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName);
                    metaByName.TryGetValue(key, out var meta);
                    return ToPortfolioApp(meta, s, screenshotVersions, scores, captureOnDemand);
                },
                StringComparer.OrdinalIgnoreCase);

        foreach (var meta in metadata
                     .Where(m => string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase))
                     .Where(m => !PortfolioIdentity.IsSelf(m.Name)))
        {
            var key = PortfolioIdentity.NormalizeName(meta.Name);
            // Catalog entries marked active are the stable showcase, visible even when
            // Azure inventory is stale or unavailable. Only added when no live service
            // already claimed the key — otherwise the live entry's status wins.
            appsByName.TryAdd(key, ToPortfolioApp(meta, null, screenshotVersions, scores, captureOnDemand));
        }

        return appsByName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Status plus 30-day uptime as an SVG badge, for each app's README. Read-only and cheap:
    /// the stored report, the history summary rows and the pinger tallies — exactly what the
    /// /azure uptime grid is built from — and none of the side effects GetPortfolio has (no
    /// auto-rescan, no screenshot capture), because a README is fetched by crawlers and CDNs
    /// that must not be able to start work on this server.
    /// </summary>
    private static async Task<IResult> GetBadge(
        string id, IWebHostEnvironment env, AzureReportStore store, UptimeSampleStore uptimeSamples,
        TimeProvider clock, HttpContext http, ILogger<Program> logger, CancellationToken ct)
    {
        var (_, services) = await LoadInventoryAsync(env, store, logger, ct);
        var metadata = await LoadMetadataAsync(env, logger, ct);
        var key = PortfolioIdentity.NormalizeName(id);
        var app = BuildApps(services, metadata, EmptyVersions, EmptyScores)
            .FirstOrDefault(a => PortfolioIdentity.NormalizeName(a.Id) == key || PortfolioIdentity.NormalizeName(a.Name) == key);
        if (app is null)
            return Results.NotFound();

        var row = RowFor(await LoadUptimeAsync(store, uptimeSamples, clock, ct), app);

        // Fills measured against white text: 5.1:1, 5.9:1, 5.6:1, 6.2:1 — the badge is text.
        var (message, color) = app.Status switch
        {
            "healthy" when row is { ScansObserved: > 0 } =>
                ($"up{Streak(row)} · {row.UptimePercent}% 30d", row.UptimePercent >= 99 ? "#2e7d32" : "#8a5300"),
            "healthy" => ("up", "#2e7d32"),
            "unavailable" => ("down", "#c62828"),
            _ => ("not monitored", "#616161"),
        };

        // Short: GitHub's image proxy re-fetches on its own schedule, and a badge that says "up"
        // for an hour after an outage started is the badge lying.
        http.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Text(StatusBadge.Render(app.Name, message, color), "image/svg+xml", System.Text.Encoding.UTF8);
    }

    /// <summary>" 12d" — the clean streak, "30d+" when the whole window was clean; empty when there is none.</summary>
    private static string Streak(UptimeRow row) => row.CleanDays switch
    {
        null or 0 => "",
        var d when d >= DashboardInsightsBuilder.UptimeWindowDays => $" {d}d+",
        var d => $" {d}d",
    };

    /// <summary>The /azure uptime grid, built from the same three sources the dashboard reads.</summary>
    private static async Task<UptimeHeatmap> LoadUptimeAsync(
        AzureReportStore store, UptimeSampleStore uptimeSamples, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var history = await store.LoadHistorySummariesAsync(maxEntries: 30, ct);
        var samples = await uptimeSamples.LoadRecentAsync(DashboardInsightsBuilder.UptimeWindowDays, now, ct);
        return DashboardInsightsBuilder.BuildUptime(
            history.IsSuccess ? history.Value ?? [] : [], now.UtcDateTime, PortfolioIdentity.IsSelf,
            DashboardInsightsBuilder.UptimeWindowDays, samples);
    }

    private static UptimeRow? RowFor(UptimeHeatmap uptime, PortfolioApp app) =>
        uptime.Rows.FirstOrDefault(r => PortfolioIdentity.NormalizeName(r.Name) == PortfolioIdentity.NormalizeName(app.Name));

    private static readonly IReadOnlyDictionary<string, long> EmptyVersions = new Dictionary<string, long>();
    private static readonly IReadOnlyDictionary<string, LighthouseScores> EmptyScores = new Dictionary<string, LighthouseScores>();

    private static async Task<IResult> GetScreenshot(
        string host, IWebHostEnvironment env, AzureReportStore store, AppScreenshotService screenshots,
        HttpContext http, ILogger<Program> logger, CancellationToken ct)
    {
        if (!HostPattern().IsMatch(host))
            return Results.BadRequest(new { error = "Invalid host." });

        var stream = await screenshots.OpenReadAsync(host, ct);
        if (stream is null && screenshots.ScreenshotsEnabled)
        {
            // On-demand capture fallback: find matching app URL from catalog or inventory
            try
            {
                var (report, _) = await LoadInventoryAsync(env, store, logger, ct);
                var target = AppScreenshotService.CombinedTargets(env, report)
                    .FirstOrDefault(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));
                if (target.Url is not null)
                {
                    logger.LogInformation("No screenshot exists for {Host} — navigating to {Url} to capture live", host, target.Url);
                    stream = await screenshots.CaptureOneAsync(host, target.Url, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live screenshot capture on demand failed for {Host}", host);
            }
        }

        if (stream is null)
            return Results.NotFound();

        // Screenshots change at most daily; let browsers cache for an hour.
        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Stream(stream, "image/png");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{0,253}$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    /// <summary>
    /// Active services from the latest Azure report — same predicate the Ops dashboard
    /// counts as Operational (HttpStatus == "active"). Falls back to the cached report
    /// file when table storage is unavailable; empty when no report exists at all.
    /// </summary>
    private static readonly JsonSerializerOptions FileReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<(AzureReport? Report, List<WebService> Services)> LoadInventoryAsync(
        IWebHostEnvironment env, AzureReportStore store, ILogger logger, CancellationToken ct)
    {
        var result = await store.LoadAsync(ct);
        var report = result.IsSuccess && result.Value is not null
            ? result.Value
            : await ReportFileCache.TryLoadFromFileAsync(env, logger, ct);

        return (report, report?.WebServices?.Services ?? new List<WebService>());
    }

    private static async Task<List<AppMeta>> LoadMetadataAsync(
        IWebHostEnvironment env, ILogger logger, CancellationToken ct)
    {
        var path = Path.Combine(ReportFileCache.GetCatalogDir(env), "apps.json");
        if (!File.Exists(path))
            return new List<AppMeta>();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var wrapper = JsonSerializer.Deserialize<AppsFile>(json, FileReadJsonOptions);
            return wrapper?.Apps ?? new List<AppMeta>();
        }
        catch (JsonException ex)
        {
            // A malformed catalog file must degrade to "no metadata", never take down the
            // home page — the class contract says the catalog stays visible during outages.
            logger.LogWarning(ex, "apps.json is malformed — serving portfolio without catalog metadata");
            return new List<AppMeta>();
        }
    }

    private static PortfolioApp ToPortfolioApp(
        AppMeta? meta, WebService? service, IReadOnlyDictionary<string, long> screenshotVersions,
        IReadOnlyDictionary<string, LighthouseScores> scores, bool captureOnDemand)
    {
        var name = meta?.Name ?? service?.FriendlyName ?? service?.Name ?? "Unnamed app";
        // The curated catalog URL wins over the scanned one: inventory can lag reality by
        // hours (or weeks), and a card must never send visitors to a decommissioned host.
        var url = !string.IsNullOrWhiteSpace(meta?.Url) ? meta.Url : service?.Url ?? "";
        var host = AppScreenshotService.HostOf(url);
        long version = 0;
        var hasScreenshot = host is not null && screenshotVersions.TryGetValue(host, out version);
        var status = service is null ? "not-monitored" :
            ServiceHealth.IsHealthy(service.HttpStatus) ? "healthy" : "unavailable";

        return new PortfolioApp
        {
            Id = meta?.Id ?? service?.Name ?? PortfolioIdentity.NormalizeName(name),
            Name = name,
            Category = meta?.Category ?? "",
            Description = !string.IsNullOrWhiteSpace(meta?.Description) ? meta.Description :
                !string.IsNullOrWhiteSpace(service?.Description) ? service.Description : $"Open {name}.",
            Url = url,
            Status = status,
            // With capture on, a missing image is captured when the card requests it.
            ScreenshotUrl = hasScreenshot || (host is not null && captureOnDemand)
                ? $"/api/portfolio/screenshots/{host}?v={version}" : null,
            Scores = host is not null && scores.TryGetValue(host, out var score) ? score : null,
            GithubRepo = string.IsNullOrWhiteSpace(meta?.GithubRepo) ? null : meta.GithubRepo,
        };
    }

    private static int StatusRank(string? status) => status?.ToLowerInvariant() switch
    {
        "active" => 0,
        "inactive" => 1,
        _ => 2,
    };

    private sealed record AppsFile(List<AppMeta>? Apps);

    private sealed record AppMeta(
        string? Id,
        string? Name,
        string? Description,
        string? Category,
        string? Status,
        string? Url,
        List<string>? Technologies,
        string? GithubRepo);
}

