namespace PoPunkouterSoftware.Infrastructure;

public enum OutageTransition { WentDown, Recovered }

/// <summary>
/// Turns the pinger's stream of per-service probe results into up/down transitions worth a
/// notification. Pure and single-threaded — the pinger feeds it after each sweep completes.
///
/// <para>Debounced: an app is only "down" after <see cref="FailuresBeforeAlert"/> consecutive
/// failed probes, and a recovery is only reported for an outage that was. One dropped packet
/// is not a page at 3am.</para>
///
/// <para>A timeout is not an observation (pass <c>null</c>): these are F1 apps that sleep, and a
/// probe missing a cold start says nothing either way — the same rule the uptime grid follows.</para>
/// </summary>
// ponytail: in-memory state, so an app still down when this site restarts is alerted again —
// a daily nag at worst, since F1 unloads when idle. Persist the alerted set if that gets noisy.
public sealed class OutageTracker(int failuresBeforeAlert = 2)
{
    public int FailuresBeforeAlert { get; } = failuresBeforeAlert;

    private readonly Dictionary<string, (int Failures, bool Alerted)> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="up">true reachable, false down, null no observation (timeout).</param>
    public OutageTransition? Observe(string service, bool? up)
    {
        if (up is null)
            return null;

        var (failures, alerted) = _state.GetValueOrDefault(service);
        if (up.Value)
        {
            _state.Remove(service);
            return alerted ? OutageTransition.Recovered : null;
        }

        failures++;
        var trips = !alerted && failures >= FailuresBeforeAlert;
        _state[service] = (failures, alerted || trips);
        return trips ? OutageTransition.WentDown : null;
    }
}
