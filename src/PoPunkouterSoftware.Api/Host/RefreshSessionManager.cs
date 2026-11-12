namespace PoPunkouterSoftware.Api;

internal sealed class RefreshSessionManager : IDisposable
{
    public readonly SemaphoreSlim Lock = new(1, 1);
    private volatile CancellationTokenSource? _activeCts;

    public void Dispose() => Lock.Dispose();

    public void SetActiveCts(CancellationTokenSource? cts) => _activeCts = cts;

    public void Cancel()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The refresh completed and disposed its CTS between our read and Cancel().
            // Nothing left to cancel — treat as a no-op instead of a 500.
        }
    }
}

