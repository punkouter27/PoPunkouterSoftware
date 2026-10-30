using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Security.Cryptography;
using System.Text;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Persists the background pinger's reachability observations as one row per service per day
/// (<see cref="TablePartitions.UptimeSamples"/>), so the <c>/azure</c> uptime grid has data on
/// days when no full Azure scan ran.
///
/// <para><b>Why a daily rollup and not a row per sweep.</b> The pinger sweeps every few
/// minutes; a row per sweep would be ~1,150 rows/day and ~35,000 across the 30-day window,
/// which the read path would have to scan on every dashboard load. A rollup is ~8 rows/day.
/// The cost is a read-modify-write per service per sweep, which is negligible and guarded by
/// an ETag so a lost update cannot silently under-count.</para>
///
/// <para>Storage failures are never fatal here: a sweep that cannot record its tally logs and
/// moves on. The pinger's real job is warming cold-start instances, and it must not stop doing
/// that because Table Storage is briefly unavailable.</para>
/// </summary>
public sealed class UptimeSampleStore
{
    /// <summary>
    /// Retries for a lost optimistic-concurrency race. Only the pinger writes this partition
    /// and its sweeps are sequential, so contention is theoretical — but an unguarded
    /// read-modify-write would under-count rather than fail loudly, which is the worst
    /// outcome for a number the UI presents as fact.
    /// </summary>
    private const int MaxConcurrencyRetries = 3;

    private readonly ILogger<UptimeSampleStore> _logger;
    private readonly TableClientProvider _tables;

    public UptimeSampleStore(ILogger<UptimeSampleStore> logger, IConfiguration config)
    {
        _logger = logger;
        _tables = new TableClientProvider(config, logger, "uptime samples");
    }

    /// <summary>
    /// Folds one sweep's results into today's tallies. Best-effort: returns false on any
    /// storage problem instead of throwing, because the caller is a background loop whose
    /// primary purpose is unrelated.
    /// </summary>
    /// <param name="observations">Service name paired with whether that probe found it healthy.</param>
    /// <param name="at">Sweep time; its UTC date selects the row.</param>
    public async Task<bool> RecordSweepAsync(
        IEnumerable<(string ServiceName, bool Healthy)> observations, DateTimeOffset at, CancellationToken ct = default)
    {
        var tableClient = await _tables.GetAsync(ct);
        if (tableClient is null)
            return false;

        var day = at.UtcDateTime.Date;
        var ok = true;

        foreach (var (serviceName, healthy) in observations)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                continue;

            try
            {
                await IncrementAsync(tableClient, day, serviceName, healthy, ct);
            }
            catch (RequestFailedException ex) when (TableClientProvider.IsTableMissing(ex))
            {
                await _tables.RecoverMissingTableAsync(ct);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record uptime sample for {Service}", serviceName);
                ok = false;
            }
        }

        return ok;
    }

    private static async Task IncrementAsync(
        TableClient tableClient, DateTime day, string serviceName, bool healthy, CancellationToken ct)
    {
        var rowKey = RowKeyFor(day, serviceName);

        for (var attempt = 0; ; attempt++)
        {
            TableEntity? existing = null;
            try
            {
                existing = await tableClient.GetEntityAsync<TableEntity>(
                    TablePartitions.UptimeSamples, rowKey, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // First observation of this service today.
            }

            var entity = new TableEntity(TablePartitions.UptimeSamples, rowKey)
            {
                ["ServiceName"] = serviceName,
                ["Day"] = new DateTimeOffset(day, TimeSpan.Zero),
                ["HealthyCount"] = (existing?.GetInt32("HealthyCount") ?? 0) + (healthy ? 1 : 0),
                ["TotalCount"] = (existing?.GetInt32("TotalCount") ?? 0) + 1,
            };

            try
            {
                if (existing is null)
                    await tableClient.AddEntityAsync(entity, ct);
                else
                    await tableClient.UpdateEntityAsync(entity, existing.ETag, TableUpdateMode.Replace, ct);
                return;
            }
            catch (RequestFailedException ex)
                when ((ex.Status == 409 || ex.Status == 412) && attempt < MaxConcurrencyRetries)
            {
                // 409 = someone else created the row between our read and Add;
                // 412 = someone else updated it. Re-read and fold again.
            }
        }
    }

    /// <summary>
    /// Every tally from the last <paramref name="days"/> days, oldest day first. An empty list
    /// is a normal answer (pinger disabled, storage down, nothing recorded yet) — the grid
    /// simply falls back to scan observations alone.
    /// </summary>
    public async Task<List<UptimeDaySample>> LoadRecentAsync(
        int days, DateTimeOffset now, CancellationToken ct = default)
    {
        var tableClient = await _tables.GetAsync(ct);
        if (tableClient is null)
            return [];

        var firstDay = now.UtcDateTime.Date.AddDays(-(days - 1));
        var samples = new List<UptimeDaySample>();

        try
        {
            // RowKey is date-prefixed ("yyyyMMdd-<hash>"), so a lexical range filter is a true
            // date range and the server never ships rows outside the window.
            var filter = $"PartitionKey eq '{TablePartitions.UptimeSamples}' and RowKey ge '{firstDay:yyyyMMdd}-'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
            {
                var name = entity.GetString("ServiceName");
                var day = entity.GetDateTimeOffset("Day");
                if (string.IsNullOrWhiteSpace(name) || day is null)
                {
                    // A corrupt/legacy row degrades to "skip it", never a 500.
                    _logger.LogWarning(
                        "Skipping corrupt uptime sample {PartitionKey}/{RowKey}", entity.PartitionKey, entity.RowKey);
                    continue;
                }

                samples.Add(new UptimeDaySample
                {
                    Day = day.Value.UtcDateTime.Date,
                    ServiceName = name,
                    HealthyCount = entity.GetInt32("HealthyCount") ?? 0,
                    TotalCount = entity.GetInt32("TotalCount") ?? 0,
                });
            }
        }
        catch (RequestFailedException ex) when (TableClientProvider.IsTableMissing(ex))
        {
            await _tables.RecoverMissingTableAsync(ct);
            return [];
        }
        catch (Exception ex)
        {
            // The uptime grid is a nice-to-have on a page that must still render.
            _logger.LogWarning(ex, "Could not load uptime samples from Table Storage");
            return [];
        }

        return samples.OrderBy(s => s.Day).ToList();
    }

    /// <summary>
    /// Deletes tallies older than the window. Called from the pinger rather than from a save
    /// path, mirroring how <see cref="AzureReportStore"/> prunes history on write — there is
    /// no scheduler in this app to hang a cleanup job on.
    /// </summary>
    public async Task PruneAsync(int retainDays, DateTimeOffset now, CancellationToken ct = default)
    {
        if (retainDays <= 0)
            return;

        var tableClient = await _tables.GetAsync(ct);
        if (tableClient is null)
            return;

        var cutoff = now.UtcDateTime.Date.AddDays(-retainDays);
        try
        {
            var filter = $"PartitionKey eq '{TablePartitions.UptimeSamples}' and RowKey lt '{cutoff:yyyyMMdd}-'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct))
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prune old uptime samples");
        }
    }

    /// <summary>
    /// <c>yyyyMMdd-&lt;hash&gt;</c>. The date prefix keeps rows lexically ordered by day so
    /// range queries work; the hash keeps the key legal, since Table Storage forbids
    /// '/', '\', '#' and '?' in a RowKey and a service name is not guaranteed to avoid them.
    /// The readable name is stored as a property.
    ///
    /// <para>Public because the format is a real contract, not an implementation detail:
    /// <see cref="LoadRecentAsync"/> and <see cref="PruneAsync"/> both rely on the date prefix
    /// sorting lexically, so it is worth asserting directly rather than only through a live
    /// Table Storage round-trip.</para>
    /// </summary>
    public static string RowKeyFor(DateTime day, string serviceName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(serviceName));
        return $"{day:yyyyMMdd}-{Convert.ToHexStringLower(bytes)[..16]}";
    }
}
