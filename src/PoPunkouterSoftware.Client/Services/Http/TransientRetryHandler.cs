using System.Net;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Minimal client-side resilience for the WASM <see cref="HttpClient"/>. The dashboard
/// reads (report / history / config) can briefly fail while the server catches up after a
/// scan; without any retry a single transient blip surfaces as a hard load error.
///
/// Deliberately bounded and trim-safe (plain <see cref="DelegatingHandler"/> — no Polly):
/// only idempotent GETs are retried, only on connection failures or transient status codes,
/// at most <see cref="MaxRetries"/> times with linear back-off. POSTs (refresh / toggle) are
/// never retried, so a control action is never silently duplicated.
/// </summary>
internal sealed class TransientRetryHandler : DelegatingHandler
{
    private const int MaxRetries = 2;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(400);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            return await base.SendAsync(request, cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (attempt >= MaxRetries || !IsTransient(response.StatusCode))
                    return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // fall through to back-off and retry
            }

            await Task.Delay(BaseDelay * (attempt + 1), cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or            // 408
        HttpStatusCode.TooManyRequests or           // 429
        HttpStatusCode.InternalServerError or       // 500
        HttpStatusCode.BadGateway or                // 502
        HttpStatusCode.ServiceUnavailable or        // 503
        HttpStatusCode.GatewayTimeout;              // 504
}
