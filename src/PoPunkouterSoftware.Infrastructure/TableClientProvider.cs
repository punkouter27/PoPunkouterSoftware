using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Shared bootstrap for the app's Table Storage client: lazy creation, a single-flight lock,
/// a failure cooldown so a storage outage does not stampede, and recovery when the table has
/// been deleted underneath a running app.
///
/// <para>Extracted when a third store (<see cref="UptimeSampleStore"/>) needed the same
/// ~80 lines the (since deleted) snooze store already carried. <see cref="AzureReportStore"/>
/// deliberately keeps its own copy for now: it layers a local-file fallback and gzip
/// handling on top, and rewiring the app's most load-bearing store was not worth the risk
/// of this change.</para>
///
/// <para>Register as a singleton per logical owner — the cooldown and cached client are
/// per-instance state.</para>
/// </summary>
public sealed class TableClientProvider
{
    private const string DefaultTableName = "PoPunkouterSoftwareReport";

    private readonly IConfiguration _config;
    private readonly ILogger _logger;
    private readonly string _owner;

    private TableClient? _cachedClient;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // When client creation fails (storage outage), short-circuit for a cooldown window instead
    // of serializing every request behind the semaphore while each pays a full connect timeout.
    private DateTimeOffset _lastClientFailureAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ClientRetryCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling on the one-time CreateIfNotExists probe, shared by every storage bootstrap here.
    /// Unbounded, the SDK's own retries against a refused port (local Azurite not running)
    /// took ~14s per table and ~37s per blob container, and /api/portfolio took 70-90s to fall
    /// back — past the client's 30s budget, so "degradation" rendered as a load failure.
    /// </summary>
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromSeconds(10);

    /// <param name="owner">Names the caller in log messages ("uptime samples").</param>
    public TableClientProvider(IConfiguration config, ILogger logger, string owner)
    {
        _config = config;
        _logger = logger;
        _owner = owner;
    }

    /// <summary>The table client, or null when storage is unavailable or unconfigured.</summary>
    public async Task<TableClient?> GetAsync(CancellationToken ct)
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

            _cachedClient = await CreateAsync(ct);
            if (_cachedClient is null)
                _lastClientFailureAt = DateTimeOffset.UtcNow;
            return _cachedClient;
        }
        catch (Exception ex)
        {
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("Table Storage unavailable for {Owner}. Reason: {Reason}", _owner, ex.Message);
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <summary>
    /// Recreates the table after a TableNotFound. Callers treat the triggering operation as
    /// failed-but-retryable rather than surfacing a 500.
    /// </summary>
    public async Task RecoverMissingTableAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Azure report table was missing. Recreating it so {Owner} reads/writes can continue.", _owner);

        await _clientLock.WaitAsync(ct);
        try
        {
            _cachedClient = await CreateAsync(ct);
        }
        catch (Exception ex)
        {
            _cachedClient = null;
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "Could not recreate missing Azure report table for {Owner}", _owner);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<TableClient?> CreateAsync(CancellationToken ct)
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
        probe.CancelAfter(BootstrapTimeout);
        await tableClient.CreateIfNotExistsAsync(probe.Token);
        return tableClient;
    }

    /// <summary>True when the failure is specifically "the table no longer exists".</summary>
    public static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && string.Equals(ex.ErrorCode, "TableNotFound", StringComparison.OrdinalIgnoreCase);
}
