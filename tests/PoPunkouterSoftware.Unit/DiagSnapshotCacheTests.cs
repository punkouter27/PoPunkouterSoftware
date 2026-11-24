using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

public class DiagSnapshotCacheTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HealthReport Report() => new(
        new Dictionary<string, HealthReportEntry>
        {
            ["KeyVault"] = new(HealthStatus.Healthy, "readable", TimeSpan.FromMilliseconds(3), null, null),
        },
        TimeSpan.FromMilliseconds(3));

    private static IReadOnlyList<DiagCheckDto> Probes() =>
        [new DiagCheckDto("github", "service", "GitHub", "Healthy", "p", "ok", 1, null)];

    /// <summary>
    /// /health and /api/diag share one round of checks for 60 s: inside the window every caller gets the same
    /// snapshot (same CheckedAt) and no check runs again; past it the next caller runs exactly one new round.
    /// </summary>
    [Fact]
    public async Task ServesOneRoundFor60Seconds_ThenRunsOneNewRound()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        int healthRuns = 0, probeRuns = 0;
        using var cache = new DiagSnapshotCache(
            _ => { Interlocked.Increment(ref healthRuns); return Task.FromResult(Report()); },
            _ => { Interlocked.Increment(ref probeRuns); return Task.FromResult(Probes()); },
            clock);

        var first = await cache.GetAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(59);
        var second = await cache.GetAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        second.CheckedAt.Should().Be(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        (healthRuns, probeRuns).Should().Be((1, 1));

        clock.Now += TimeSpan.FromSeconds(1);
        var third = await cache.GetAsync(CancellationToken.None);

        third.CheckedAt.Should().Be(clock.Now);
        (healthRuns, probeRuns).Should().Be((2, 2));
    }

    /// <summary>A burst of concurrent visitors on a cold cache costs one round, not one per request.</summary>
    [Fact]
    public async Task ConcurrentCallersOnAColdCache_ShareOneRound()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<HealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthRuns = 0;
        using var cache = new DiagSnapshotCache(
            _ => { Interlocked.Increment(ref healthRuns); return release.Task; },
            _ => Task.FromResult(Probes()),
            clock);

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetAsync(CancellationToken.None)).ToArray();
        release.SetResult(Report());
        var results = await Task.WhenAll(callers);

        healthRuns.Should().Be(1);
        results.Should().AllSatisfy(r => r.Should().BeSameAs(results[0]));
    }

    /// <summary>
    /// A caller that gives up (closed tab) stops waiting, but the round itself runs on without its token,
    /// so it is not cancelled for the callers still waiting.
    /// </summary>
    [Fact]
    public async Task ChecksRunWithoutTheCallersToken_AndACancelledCallerOnlyStopsWaiting()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<HealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        using var cache = new DiagSnapshotCache(
            ct => { seen = ct; return release.Task; },
            _ => Task.FromResult(Probes()),
            clock);
        using var gaveUp = new CancellationTokenSource();

        var leaving = cache.GetAsync(gaveUp.Token);
        await gaveUp.CancelAsync();

        await FluentActions.Awaiting(() => leaving).Should().ThrowAsync<OperationCanceledException>();
        seen.CanBeCanceled.Should().BeFalse();
    }
}
