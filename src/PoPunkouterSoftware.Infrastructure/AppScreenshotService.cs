using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Captures mobile-portrait home-page screenshots of the live portfolio apps with headless
/// Chromium and persists them in Azure Blob Storage (container "app-screenshots", one PNG
/// per host). Capture runs during the ops Azure refresh and, for any host whose screenshot
/// is missing or over a week old, when the home page is loaded — visitors always get the stored
/// (possibly stale) image immediately; capture never blocks a request.
/// Register as a singleton: it caches the blob container client and serialises capture runs.
/// </summary>
public class AppScreenshotService
{
    private const string ContainerName = "app-screenshots";

    /// <summary>iPhone-class portrait viewport; cards display the PNG at 20% (78×169).</summary>
    private const int ViewportWidth = 390;
    private const int ViewportHeight = 844;

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(30);

    private readonly ILogger<AppScreenshotService> _logger;
    private readonly IConfiguration _config;

    private BlobContainerClient? _cachedContainer;
    private readonly SemaphoreSlim _containerLock = new(1, 1);

    // Same failure cooldown the table stores carry. Without it, /api/portfolio's two blob
    // reads (versions + scores) queued on the lock and each paid the full retry walk in turn.
    private DateTimeOffset _lastContainerFailureAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ContainerRetryCooldown = TimeSpan.FromSeconds(30);

    // Only one capture run at a time; page loads that find a run in progress just skip.
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    // Throttles retries when capture keeps failing (e.g. Chromium missing on the host),
    // so a hot home page cannot hammer Playwright. In-memory only — resets on restart.
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOnDemandFailureUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// On Azure App Service the default Playwright install location is the worker's
    /// ephemeral local disk (%USERPROFILE%), which is tiny and shared with Kudu's deploy
    /// extraction — a Chromium download there exhausted it and every deployment failed
    /// with "not enough space on the disk" (2026-07-10). Pin installs to the persistent
    /// HOME share instead so the browser survives restarts and stays off the deploy path.
    /// </summary>
    static AppScreenshotService()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")))
        {
            Environment.SetEnvironmentVariable(
                "PLAYWRIGHT_BROWSERS_PATH", Path.Combine(home, ".playwright-browsers"));
        }
    }

    private readonly string _localDir;

    public AppScreenshotService(ILogger<AppScreenshotService> logger, IConfiguration config, IWebHostEnvironment env)
    {
        _logger = logger;
        _config = config;
        _localDir = ReportFileCache.GetScreenshotsDir(env);
    }

    /// <summary>
    /// Kill switch for constrained hosts (F1 quota pressure): screenshots are on by
    /// default and disabled with FeatureFlags:EnableScreenshots=false. Stored images
    /// keep serving either way — only staleness checks and new captures stop.
    /// </summary>
    public bool ScreenshotsEnabled => _config.GetValue("FeatureFlags:EnableScreenshots", true);

    /// <summary>The active (HTTP-reachable) services of a report as capture targets.</summary>
    public static List<(string Host, string Url)> ActiveTargets(AzureReport? report) =>
        (report?.WebServices?.Services ?? new List<WebService>())
            .Where(s => string.Equals(s.HttpStatus, "active", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Host: HostOf(s.Url), s.Url))
            .Where(t => t.Host is not null)
            .Select(t => (t.Host!, t.Url))
            .ToList();

    /// <summary>
    /// Hosts that the curated apps.json lists as active, ignoring any entries the scan
    /// already covered. Mirrors the GitHub Actions `screenshots.yml` workflow, which also
    /// captures directly from apps.json — so a dev environment with no live Azure report
    /// can still produce previews. Returns <c>[]</c> when the catalog is missing or
    /// malformed: capture is non-fatal and the file-cache loader degrades the same way.
    /// </summary>
    public static List<(string Host, string Url)> CatalogTargets(IWebHostEnvironment env)
    {
        try
        {
            var path = Path.Combine(ReportFileCache.GetCatalogDir(env), "apps.json");
            if (!File.Exists(path))
                return [];

            using var stream = File.OpenRead(path);
            var doc = System.Text.Json.JsonDocument.Parse(stream,
                new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            // PropertyNameCaseInsensitive on Parse isn't a thing; the loader below uses it.
            var list = new List<(string Host, string Url)>();
            if (!doc.RootElement.TryGetProperty("apps", out var apps)
                || apps.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;

            foreach (var entry in apps.EnumerateArray())
            {
                var status = entry.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
                var host = HostOf(url);
                if (host is null) continue;
                list.Add((host, url!));
            }
            return list;
        }
        catch (Exception)
        {
            // apps.json is authoritative for the catalogue; a malformed file must not
            // crash the screenshot path — the same posture the portfolio endpoint uses
            // when loading its metadata.
            return [];
        }
    }

    /// <summary>
    /// Combined capture set: scan-derived active services first (so a healthy active host
    /// always takes priority over its catalog entry), then any catalog-only entries so a
    /// fresh dev environment still gets previews without a populated report cache.
    /// Deduped on host.
    /// </summary>
    public static List<(string Host, string Url)> CombinedTargets(IWebHostEnvironment env, AzureReport? report)
    {
        var scanTargets = ActiveTargets(report);
        var seen = new HashSet<string>(scanTargets.Select(t => t.Host), StringComparer.OrdinalIgnoreCase);
        var combined = new List<(string Host, string Url)>(scanTargets);
        foreach (var t in CatalogTargets(env))
        {
            if (seen.Add(t.Host))
                combined.Add(t);
        }
        return combined;
    }

    /// <summary>
    /// The targets whose screenshot is missing or older than a week, judged per host so a
    /// newly added app is captured even while every other preview is fresh. Empty while
    /// disabled or within 30 minutes of the last attempt. <paramref name="versions"/> is
    /// <see cref="ListVersionsAsync"/>'s output (host → last-modified unix seconds).
    /// </summary>
    public List<(string Host, string Url)> DueForCapture(
        IEnumerable<(string Host, string Url)> targets, IReadOnlyDictionary<string, long> versions)
    {
        var now = DateTimeOffset.UtcNow;
        if (!ScreenshotsEnabled || now - _lastAttemptUtc < AttemptCooldown)
            return [];

        return targets
            .Where(t => !versions.TryGetValue(t.Host, out var v) || now - DateTimeOffset.FromUnixTimeSeconds(v) > MaxAge)
            .ToList();
    }

    /// <summary>Hosts that currently have a stored screenshot.</summary>
    public async Task<HashSet<string>> ListHostsAsync(CancellationToken ct = default)
        => (await ListVersionsAsync(ct)).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stored screenshot hosts and a cache-busting version from blob modification time.</summary>
    public async Task<Dictionary<string, long>> ListVersionsAsync(CancellationToken ct = default)
    {
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Local disk copy first; blob entries (when configured) override it below.
        try
        {
            if (Directory.Exists(_localDir))
            {
                foreach (var file in Directory.EnumerateFiles(_localDir, "*.png"))
                    versions[Path.GetFileNameWithoutExtension(file)] = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check local screenshots directory");
        }

        var container = await GetContainerAsync(ct);
        if (container is null)
            return versions;

        try
        {
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
            {
                if (blob.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    versions[blob.Name[..^4]] = blob.Properties.LastModified?.ToUnixTimeSeconds() ?? 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list app screenshots from blob storage");
        }
        return versions;
    }

    /// <summary>
    /// Per-host PageSpeed Insights scores from <c>lighthouse.json</c>, which
    /// <c>.github/workflows/screenshots.yml</c> writes nightly into this same container.
    /// Empty when the file is absent or unreadable — a card simply shows no scores.
    /// </summary>
    public async Task<Dictionary<string, LighthouseScores>> LoadScoresAsync(CancellationToken ct = default)
    {
        var empty = new Dictionary<string, LighthouseScores>(StringComparer.OrdinalIgnoreCase);
        var container = await GetContainerAsync(ct);
        if (container is null)
            return empty;

        try
        {
            var content = await container.GetBlobClient(ScoresBlobName).DownloadContentAsync(ct);
            var file = content.Value.Content.ToObjectFromJson<LighthouseFile>(ScoresJsonOptions);
            return new Dictionary<string, LighthouseScores>(file?.Scores ?? empty, StringComparer.OrdinalIgnoreCase);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {Blob}", ScoresBlobName);
            return empty;
        }
    }

    private const string ScoresBlobName = "lighthouse.json";

    private static readonly System.Text.Json.JsonSerializerOptions ScoresJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private sealed record LighthouseFile(Dictionary<string, LighthouseScores>? Scores);

    /// <summary>Opens a stored screenshot for streaming, or null when missing/unavailable.</summary>
    public async Task<Stream?> OpenReadAsync(string host, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        if (container is not null)
        {
            try
            {
                var blob = container.GetBlobClient($"{host}.png");
                if (await blob.ExistsAsync(ct))
                    return await blob.OpenReadAsync(cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read screenshot for {Host} from blob storage", host);
            }
        }

        // Local filesystem fallback
        try
        {
            var localPath = Path.Combine(_localDir, $"{host}.png");
            if (File.Exists(localPath))
                return File.OpenRead(localPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read local screenshot for {Host}", host);
        }

        return null;
    }

    /// <summary>
    /// Captures and stores a screenshot per target. Non-fatal throughout: individual app
    /// failures are skipped, and if another capture run is already active this returns
    /// immediately. Safe to fire-and-forget.
    /// </summary>
    public async Task CaptureAsync(IReadOnlyList<(string Host, string Url)> targets, CancellationToken ct = default)
    {
        if (!ScreenshotsEnabled)
        {
            _logger.LogDebug("Screenshot capture skipped — disabled via FeatureFlags:EnableScreenshots");
            return;
        }

        if (targets.Count == 0 || !await _captureLock.WaitAsync(0, ct))
            return;

        try
        {
            _lastAttemptUtc = DateTimeOffset.UtcNow;
            var container = await GetContainerAsync(ct);

            using var pw = await CreatePlaywrightWithBrowserAsync();
            if (pw is null)
                return;

            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            var captured = 0;

            foreach (var (host, url) in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await CaptureAndStoreAsync(browser, container, host, url, ct);
                    captured++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Screenshot failed for {Host}", host);
                }
            }

            _logger.LogInformation("Captured {Captured}/{Total} app screenshots", captured, targets.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Screenshot capture run failed");
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>
    /// Captures a screenshot on-demand for a single target, saving to both local disk and blob storage.
    /// </summary>
    public async Task<Stream?> CaptureOneAsync(string host, string url, CancellationToken ct = default)
    {
        // Never queue an image request behind a batch run (N × 30s): skip instead, and the card
        // shows its placeholder until the batch fills the gap. After a failure, wait out the
        // same cooldown DueForCapture uses so a broken Playwright setup is not retried per request.
        if (!ScreenshotsEnabled
            || DateTimeOffset.UtcNow - _lastOnDemandFailureUtc < AttemptCooldown
            || !await _captureLock.WaitAsync(0, ct))
            return null;

        try
        {
            var container = await GetContainerAsync(ct);
            using var pw = await CreatePlaywrightWithBrowserAsync();
            if (pw is null)
            {
                _lastOnDemandFailureUtc = DateTimeOffset.UtcNow;
                return null;
            }

            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            return new MemoryStream(await CaptureAndStoreAsync(browser, container, host, url, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastOnDemandFailureUtc = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "On-demand screenshot capture failed for {Host}", host);
            return null;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>
    /// Captures one page and persists it to blob storage (when configured) and the local disk
    /// copy. Storage failures are logged, not thrown — the caller still gets the PNG.
    /// </summary>
    private async Task<byte[]> CaptureAndStoreAsync(
        IBrowser browser, BlobContainerClient? container, string host, string url, CancellationToken ct)
    {
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = ViewportWidth, Height = ViewportHeight },
            IsMobile = true,
            HasTouch = true,
            DeviceScaleFactor = 2,
        });
        byte[] png;
        try
        {
            // Cold Azure apps can take a while; capture whatever rendered on timeout.
            try
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            }
            catch (System.TimeoutException) { }

            png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png });
        }
        finally
        {
            await page.CloseAsync();
        }

        if (container is not null)
        {
            try
            {
                await container.GetBlobClient($"{host}.png").UploadAsync(
                    new BinaryData(png),
                    new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" } },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upload screenshot to blob storage for {Host}", host);
            }
        }

        try
        {
            Directory.CreateDirectory(_localDir);
            await File.WriteAllBytesAsync(Path.Combine(_localDir, $"{host}.png"), png, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write local screenshot for {Host}", host);
        }

        return png;
    }

    /// <summary>
    /// Creates Playwright, installing the Chromium headless shell on first use if missing.
    /// Returns null (logged) when the environment cannot run a browser at all.
    /// </summary>
    private async Task<IPlaywright?> CreatePlaywrightWithBrowserAsync()
    {
        var pw = await Playwright.CreateAsync();
        try
        {
            await using var probe = await pw.Chromium.LaunchAsync(new() { Headless = true });
            return pw;
        }
        catch (PlaywrightException)
        {
            // Headless shell only — roughly a third of the full Chromium footprint, which
            // matters on the 1 GB App Service home share. Headless launches use it directly.
            _logger.LogInformation("Chromium not found — installing Playwright headless shell (one-time)");
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium-headless-shell" }));
            if (exitCode == 0)
                return pw;

            pw.Dispose();
            _logger.LogWarning("Playwright browser install failed (exit {ExitCode}) — screenshots disabled", exitCode);
            return null;
        }
    }

    private async Task<BlobContainerClient?> GetContainerAsync(CancellationToken ct)
    {
        if (_cachedContainer is not null)
            return _cachedContainer;
        if (DateTimeOffset.UtcNow - _lastContainerFailureAt < ContainerRetryCooldown)
            return null;

        await _containerLock.WaitAsync(ct);
        try
        {
            if (_cachedContainer is not null)
                return _cachedContainer;
            if (DateTimeOffset.UtcNow - _lastContainerFailureAt < ContainerRetryCooldown)
                return null;

            // Same account as the report table store; a dedicated blob endpoint may be
            // configured for managed-identity setups where only endpoints are known.
            var connectionString = _config["AzureTableStorage:ConnectionString"];
            BlobServiceClient service;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                service = new BlobServiceClient(connectionString);
            }
            else
            {
                var endpoint = _config["AzureBlobStorage:Endpoint"];
                if (string.IsNullOrWhiteSpace(endpoint))
                    return null;
                service = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
            }

            var container = service.GetBlobContainerClient(ContainerName);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TableClientProvider.BootstrapTimeout);
            await container.CreateIfNotExistsAsync(cancellationToken: probe.Token);
            _cachedContainer = container;
            return _cachedContainer;
        }
        catch (Exception ex)
        {
            _lastContainerFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("Blob storage unavailable — screenshots disabled. Reason: {Reason}", ex.Message);
            return null;
        }
        finally
        {
            _containerLock.Release();
        }
    }

    /// <summary>Lowercased host from a URL, or null when the URL is missing/invalid. Shared with PortfolioEndpoints.</summary>
    public static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
}
