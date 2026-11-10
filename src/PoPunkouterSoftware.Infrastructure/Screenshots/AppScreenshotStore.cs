using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Reads the home page's app screenshots and PageSpeed scores from the "app-screenshots" blob
/// container. Read-only: <c>.github/workflows/screenshots.yml</c> is the only writer (one PNG per
/// host, plus <c>lighthouse.json</c>), nightly.
///
/// <para>Until 2026-11-07 this was <c>AppScreenshotService</c> and ALSO captured, in-process,
/// with Playwright: after every scan, on a home-page load when an image was a week old, and on
/// demand when a card asked for a missing one. That was a second implementation of the same
/// capture (a WebGL launch fix had to be made in both), it could not run in Production at all
/// (the F1 sandbox has no Chromium, and an install once filled the worker disk), and it put a
/// headless browser behind an anonymous endpoint. Locally, cards show what Azurite already
/// holds; a new app gets its picture after the next nightly run.</para>
///
/// Register as a singleton: it caches the container client.
/// </summary>
public class AppScreenshotStore(ILogger<AppScreenshotStore> logger, IConfiguration config)
{
    private const string ContainerName = "app-screenshots";
    private const string ScoresBlobName = "lighthouse.json";

    private BlobContainerClient? _cachedContainer;
    private readonly SemaphoreSlim _containerLock = new(1, 1);

    // Same failure cooldown the table stores carry. Without it, /api/portfolio's two blob
    // reads (versions + scores) queued on the lock and each paid the full retry walk in turn.
    private DateTimeOffset _lastContainerFailureAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ContainerRetryCooldown = TimeSpan.FromSeconds(30);

    private static readonly System.Text.Json.JsonSerializerOptions ScoresJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private sealed record LighthouseFile(Dictionary<string, LighthouseScores>? Scores);

    /// <summary>Stored screenshot hosts and a cache-busting version from blob modification time.</summary>
    public async Task<Dictionary<string, long>> ListVersionsAsync(CancellationToken ct = default)
    {
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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
            logger.LogWarning(ex, "Could not list app screenshots from blob storage");
        }
        return versions;
    }

    /// <summary>
    /// Per-host PageSpeed Insights scores from <c>lighthouse.json</c>. Empty when the file is
    /// absent or unreadable — a card simply shows no scores.
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
            logger.LogWarning(ex, "Could not read {Blob}", ScoresBlobName);
            return empty;
        }
    }

    /// <summary>Opens a stored screenshot for streaming, or null when missing/unavailable.</summary>
    public async Task<Stream?> OpenReadAsync(string host, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        if (container is null)
            return null;

        try
        {
            var blob = container.GetBlobClient($"{host}.png");
            if (await blob.ExistsAsync(ct))
                return await blob.OpenReadAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read screenshot for {Host} from blob storage", host);
        }
        return null;
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

            // Same account as the report table store; a dedicated blob endpoint is configured
            // for the managed-identity path, where only endpoints are known.
            var connectionString = config["AzureTableStorage:ConnectionString"];
            BlobServiceClient service;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                service = new BlobServiceClient(connectionString);
            }
            else
            {
                var endpoint = config["AzureBlobStorage:Endpoint"];
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
            logger.LogWarning("Blob storage unavailable — screenshots disabled. Reason: {Reason}", ex.Message);
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
