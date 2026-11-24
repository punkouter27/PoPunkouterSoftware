using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>One round of checks: the health-check report, the outside-service probe rows, and when they ran.</summary>
public sealed record DiagSnapshot(HealthReport Health, IReadOnlyList<DiagCheckDto> Probes, DateTimeOffset CheckedAt);

/// <summary>
/// The one place the app's dependency checks run. <c>GET /health</c> and <c>GET /api/diag</c> (the /diag page) both
/// read the same snapshot, which is kept for <see cref="CacheFor"/>: an anonymous visitor reloading /diag, or a
/// monitor polling /health, costs at most one round of checks a minute against Key Vault, Table Storage, ARM,
/// GitHub, NuGet and the rest. Every health check carries its own timeout (Program.cs, <c>CheckTimeout</c>) and
/// every probe its own 5 s budget, so one slow service cannot hold the round.
///
/// <para>The round runs without the request's cancellation token: a visitor closing the tab must not cancel
/// (and so poison) the round other requests are waiting on. Callers only stop waiting.</para>
/// </summary>
public sealed class DiagSnapshotCache : IDisposable
{
    /// <summary>How long one round of checks is served before the next request runs a fresh one.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>Per health check budget; a check past it reports Unhealthy-by-timeout instead of hanging.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<HealthReport>> _health;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiagCheckDto>>> _probes;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiagSnapshot? _current;

    public DiagSnapshotCache(
        Func<CancellationToken, Task<HealthReport>> health,
        Func<CancellationToken, Task<IReadOnlyList<DiagCheckDto>>> probes,
        TimeProvider clock)
    {
        _health = health;
        _probes = probes;
        _clock = clock;
    }

    public async Task<DiagSnapshot> GetAsync(CancellationToken ct)
    {
        if (Fresh(_current) is { } hit)
            return hit;

        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh(_current) is { } raced)
                return raced;

            var checkedAt = _clock.GetUtcNow();
            var health = _health(CancellationToken.None);
            var probes = _probes(CancellationToken.None);
            await Task.WhenAll(health, probes).WaitAsync(ct);
            return _current = new DiagSnapshot(await health, await probes, checkedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DiagSnapshot? Fresh(DiagSnapshot? snapshot) =>
        snapshot is not null && _clock.GetUtcNow() - snapshot.CheckedAt < CacheFor ? snapshot : null;

    public void Dispose() => _gate.Dispose();
}
