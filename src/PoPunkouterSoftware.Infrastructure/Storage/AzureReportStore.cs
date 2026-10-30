using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Persists the latest Azure report as a single entity in Azure Table Storage.
/// Uses the app-specific storage account (not the shared PoShared one).
/// </summary>
public class AzureReportStore
{
    private const string DefaultTableName = "PoPunkouterSoftwareReport";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ILogger<AzureReportStore> _logger;
    private readonly IConfiguration _config;

    // Cached after first successful init so CreateIfNotExistsAsync is not called on every request.
    private TableClient? _cachedClient;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // When client creation fails (storage outage), short-circuit for a cooldown window instead
    // of serializing every request behind the semaphore while each pays a full connect timeout.
    private DateTimeOffset _lastClientFailureAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ClientRetryCooldown = TimeSpan.FromSeconds(30);

    // The report changes at most once per scan; every hot GET (portfolio home page, diag
    // report/summary) used to re-download + gunzip + deserialize the full multi-MB graph.
    // A short-TTL cache of the deserialized report removes ~all of that per-request cost.
    private sealed record CachedReport(AzureReport? Report, DateTimeOffset At);
    private volatile CachedReport? _reportCache;
    private static readonly TimeSpan ReportCacheTtl = TimeSpan.FromSeconds(60);

    public AzureReportStore(ILogger<AzureReportStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<Result<AzureReport?>> LoadAsync(CancellationToken ct = default)
    {
        var cached = _reportCache;
        if (cached is not null && DateTimeOffset.UtcNow - cached.At < ReportCacheTtl)
            return Result<AzureReport?>.Success(cached.Report);

        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<AzureReport?>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            var response = await tableClient.GetEntityIfExistsAsync<TableEntity>(
                TablePartitions.Report, TableRowKeys.Latest, cancellationToken: ct);
            if (!response.HasValue)
            {
                _reportCache = new CachedReport(null, DateTimeOffset.UtcNow);
                return Result<AzureReport?>.Success(null);
            }

            var report = DeserializeReportEntity(response.Value!);
            _reportCache = new CachedReport(report, DateTimeOffset.UtcNow);
            return Result<AzureReport?>.Success(report);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<AzureReport?>.Success(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport from Table Storage");
            return Result<AzureReport?>.Failure("Failed to load AzureReport from Table Storage", ex);
        }
    }

    public async Task<Result<bool>> SaveAsync(AzureReport report, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
        {
            _logger.LogWarning("Table Storage not configured — report will not be persisted");
            return Result<bool>.Failure("Table Storage not configured");
        }

        try
        {
            var json = JsonSerializer.Serialize(report, _jsonOptions);
            var compressed = CompressJson(json);

            // Table Storage caps a single property at 64 KiB. Binary storage (rather than the
            // previous base64 string) buys 33% headroom, but a grown subscription can still
            // exceed it — surface that loudly instead of silently freezing the dashboard on
            // the last good report.
            if (compressed.Length > 60_000)
                _logger.LogWarning(
                    "Compressed AzureReport is {Bytes} bytes — approaching the 64 KiB Table Storage property limit; consider moving report persistence to Blob storage",
                    compressed.Length);

            var savedAt = DateTimeOffset.UtcNow;
            var entity = new TableEntity(TablePartitions.Report, TableRowKeys.Latest)
            {
                ["ReportGz"] = compressed,
                ["SavedAt"] = savedAt,
            };

            await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

            // Also save a timestamped history entry (inverse ticks = newest first in queries)
            var histRowKey = new ReverseChronoRowKey(savedAt).ToString();
            var historyEntity = new TableEntity(TablePartitions.History, histRowKey)
            {
                ["ReportGz"] = compressed,
                ["SavedAt"] = savedAt,
            };
            await tableClient.UpsertEntityAsync(historyEntity, TableUpdateMode.Replace, ct);

            // Precomputed chart summary: tiny row the history endpoints can read without
            // decompressing full report blobs (non-fatal if it fails; readers fall back).
            try
            {
                var summaryEntity = new TableEntity(TablePartitions.HistorySummary, histRowKey)
                {
                    ["SummaryJson"] = JsonSerializer.Serialize(HistorySummaryMapper.FromReport(report), _jsonOptions),
                    ["SavedAt"] = savedAt,
                };
                await tableClient.UpsertEntityAsync(summaryEntity, TableUpdateMode.Replace, ct);
            }
            catch (Exception sumEx)
            {
                _logger.LogWarning(sumEx, "Failed to save history summary row (non-fatal)");
            }

            // Age out old history rows so the append-only partitions cannot grow without
            // bound. Piggybacks on the (at most once per scan) save; non-fatal.
            try
            {
                await PruneHistoryAsync(tableClient, ct);
            }
            catch (Exception pruneEx)
            {
                _logger.LogWarning(pruneEx, "History retention sweep failed (non-fatal)");
            }

            _reportCache = new CachedReport(report, DateTimeOffset.UtcNow);
            _logger.LogInformation("AzureReport saved to Table Storage (including history)");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AzureReport to Table Storage");
            return Result<bool>.Failure("Failed to save AzureReport to Table Storage", ex);
        }
    }

    /// <summary>
    /// Deletes History/HistorySummary rows older than Retention:HistoryDays (default 30;
    /// 0 or negative disables). Inverse-ticks row keys sort newest-first, so rows OLDER
    /// than the cutoff have row keys lexically GREATER than the cutoff's key — an
    /// efficient range scan rather than a full-partition sweep. Incidents are deliberately
    /// NOT pruned: the incident log is user-facing history the owner curates.
    /// Capped per run; the remainder ages out on subsequent scans.
    /// </summary>
    private async Task PruneHistoryAsync(TableClient tableClient, CancellationToken ct)
    {
        var days = _config.GetValue("Retention:HistoryDays", 30);
        if (days <= 0)
            return;

        var cutoffKey = new ReverseChronoRowKey(DateTimeOffset.UtcNow.AddDays(-days)).ToString();
        var deleted = 0;
        foreach (var partition in new[] { TablePartitions.History, TablePartitions.HistorySummary })
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{partition}' and RowKey gt '{cutoffKey}'",
                maxPerPage: 100,
                select: new[] { "PartitionKey", "RowKey" },
                cancellationToken: ct))
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
                if (++deleted >= 200)
                {
                    _logger.LogInformation("History retention: per-run delete cap reached ({Deleted}); remainder next scan", deleted);
                    return;
                }
            }
        }

        if (deleted > 0)
            _logger.LogInformation("History retention: deleted {Deleted} rows older than {Days} days", deleted, days);
    }

    public async Task<Result<List<AzureReport>>> LoadHistoryAsync(int maxEntries = 90, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<List<AzureReport>>.Failure("Table client not available - check Azure Table Storage configuration.");

        var results = new List<AzureReport>();
        try
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{TablePartitions.History}'",
                maxPerPage: maxEntries,
                cancellationToken: ct))
            {
                var report = DeserializeReportEntity(entity);
                if (report is not null)
                    results.Add(report);
                if (results.Count >= maxEntries)
                    break;
            }
            return Result<List<AzureReport>>.Success(results);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<List<AzureReport>>.Success(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport history from Table Storage");
            return Result<List<AzureReport>>.Failure("Failed to load AzureReport history from Table Storage", ex);
        }
    }

    /// <summary>
    /// Loads precomputed per-scan summaries (newest first). Falls back to projecting full
    /// history blobs for scans persisted before summary rows existed.
    /// </summary>
    public async Task<Result<List<HistorySummary>>> LoadHistorySummariesAsync(int maxEntries = 90, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<List<HistorySummary>>.Failure("Table client not available - check Azure Table Storage configuration.");

        var results = new List<HistorySummary>();
        try
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{TablePartitions.HistorySummary}'",
                maxPerPage: maxEntries,
                cancellationToken: ct))
            {
                var json = entity.GetString("SummaryJson");
                if (string.IsNullOrWhiteSpace(json))
                    continue;
                try
                {
                    var summary = JsonSerializer.Deserialize<HistorySummary>(json, _jsonOptions);
                    if (summary is not null)
                        results.Add(summary);
                }
                catch (JsonException jex)
                {
                    _logger.LogWarning(jex, "Skipping corrupt history summary row {RowKey}", entity.RowKey);
                }
                if (results.Count >= maxEntries)
                    break;
            }

            if (results.Count > 0)
                return Result<List<HistorySummary>>.Success(results);

            // Legacy data path: no summary rows yet — project the full blobs once. The
            // summary query above already succeeded, so storage is reachable; a failure
            // here just means there is no projectable legacy data. "No history yet" must
            // render as an empty chart, not a 503 error banner.
            var historyResult = await LoadHistoryAsync(maxEntries, ct);
            if (!historyResult.IsSuccess)
            {
                _logger.LogWarning("Legacy history projection unavailable ({Error}) — returning empty history", historyResult.Error);
                return Result<List<HistorySummary>>.Success(results);
            }

            return Result<List<HistorySummary>>.Success(
                (historyResult.Value ?? new()).Select(HistorySummaryMapper.FromReport).ToList());
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<List<HistorySummary>>.Success(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load history summaries from Table Storage");
            return Result<List<HistorySummary>>.Failure("Failed to load history summaries from Table Storage", ex);
        }
    }

    public async Task<Result<AzureReport?>> LoadPreviousAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<AzureReport?>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            // History RowKey uses inverse ticks so the first result is the most recent entry
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{TablePartitions.History}'",
                maxPerPage: 1,
                cancellationToken: ct))
            {
                return Result<AzureReport?>.Success(DeserializeReportEntity(entity));
            }
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<AzureReport?>.Success(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load previous AzureReport from history");
            return Result<AzureReport?>.Failure("Failed to load previous AzureReport from history", ex);
        }
        return Result<AzureReport?>.Success(null);
    }

    private static byte[] CompressJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Deserializes a report entity, newest format first: raw gzip bytes ("ReportGz"),
    /// then legacy base64-gzip string ("ReportJsonGz"), then legacy plain JSON ("ReportJson").
    /// Streams the gzip payload straight into the serializer — no intermediate LOH string.
    /// </summary>
    private AzureReport? DeserializeReportEntity(TableEntity entity)
    {
        try
        {
            var raw = entity.GetBinary("ReportGz");
            if (raw is { Length: > 0 })
            {
                using var input = new MemoryStream(raw);
                using var gz = new GZipStream(input, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<AzureReport>(gz, _jsonOptions);
            }

            var compressed = entity.GetString("ReportJsonGz");
            if (!string.IsNullOrWhiteSpace(compressed))
            {
                using var input = new MemoryStream(Convert.FromBase64String(compressed));
                using var gz = new GZipStream(input, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<AzureReport>(gz, _jsonOptions);
            }

            var json = entity.GetString("ReportJson");
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            // A corrupt row must degrade to "no report", never a 500.
            _logger.LogWarning(ex, "Skipping corrupt report entity {PartitionKey}/{RowKey}", entity.PartitionKey, entity.RowKey);
            return null;
        }
    }

    private async Task<TableClient?> GetTableClientAsync(CancellationToken ct)
    {
        if (_cachedClient is not null)
            return _cachedClient;

        // Storage is down or misconfigured: don't stampede — one caller retries per cooldown.
        if (DateTimeOffset.UtcNow - _lastClientFailureAt < ClientRetryCooldown)
            return null;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_cachedClient is not null)
                return _cachedClient;
            if (DateTimeOffset.UtcNow - _lastClientFailureAt < ClientRetryCooldown)
                return null;

            _cachedClient = await CreateTableClientAsync(ct);
            if (_cachedClient is null)
                _lastClientFailureAt = DateTimeOffset.UtcNow;
            return _cachedClient;
        }
        catch (Exception ex)
        {
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("Table Storage unavailable — report will fall back to file. Reason: {Reason}", ex.Message);
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<TableClient?> CreateTableClientAsync(CancellationToken ct)
    {
        var tableName = _config["AzureTableStorage:TableName"] ?? DefaultTableName;
        var connectionString = _config["AzureTableStorage:ConnectionString"];

        TableServiceClient serviceClient;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            serviceClient = new TableServiceClient(connectionString);
        }
        else
        {
            var endpoint = _config["AzureTableStorage:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            serviceClient = new TableServiceClient(new Uri(endpoint), new DefaultAzureCredential());
        }

        var tableClient = serviceClient.GetTableClient(tableName);
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TableClientProvider.BootstrapTimeout);
        await tableClient.CreateIfNotExistsAsync(probe.Token);
        return tableClient;
    }

    private async Task RecoverMissingTableAsync(CancellationToken ct)
    {
        _logger.LogInformation("Azure report table was missing. Recreating it so reads can continue without file-only fallback.");

        await _clientLock.WaitAsync(ct);
        try
        {
            _cachedClient = await CreateTableClientAsync(ct);
        }
        catch (Exception ex)
        {
            _cachedClient = null;
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "Could not recreate missing Azure report table");
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && string.Equals(ex.ErrorCode, "TableNotFound", StringComparison.OrdinalIgnoreCase);
}
