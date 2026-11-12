using System.Net;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Turns a caught exception into a sentence a visitor can act on.
///
/// <para>Both pages used to render <c>ex.Message</c> verbatim into their error banner. For the
/// most common failure on either page — a non-success HTTP status from
/// <c>GetFromJsonAsync</c> — that message is <c>HttpRequestException</c>'s formatted resource
/// string, and under the WASM runtime's sharded ICU data it surfaces as the raw resource KEY:
/// a 503 on <c>/api/diag/summary</c> printed
/// <c>net_http_message_not_success_statuscode_reason, 503, Service Unavailable</c>
/// across the top of the dashboard. That is framework internals shown as user-facing copy.</para>
///
/// <para>Everything here is a mapping, never a swallow: the original exception is still what
/// the caller logs, and an unrecognised failure falls through to its own message rather than
/// to a generic "something went wrong" that hides a real diagnostic.</para>
/// </summary>
internal static class FriendlyError
{
    internal static string Describe(Exception ex) => ex switch
    {
        // Ordered narrowest-first: a client-side timeout arrives as TaskCanceledException,
        // which derives from OperationCanceledException, so the timeout test must run before
        // the general cancellation one or every timeout reads as "cancelled".
        TaskCanceledException or TimeoutException =>
            "The server did not respond in time. It may be waking up from idle — try again in a moment.",

        HttpRequestException http => DescribeHttp(http),

        OperationCanceledException =>
            "The request was cancelled before it finished.",

        // GetFromJsonAsync throws this for a 200 whose body will not bind — a contract
        // mismatch between a freshly deployed server and an already-loaded WASM bundle is the
        // realistic cause, and reloading is genuinely the fix.
        System.Text.Json.JsonException =>
            "The server sent a response this page could not read. Reload to pick up the current build.",

        InvalidOperationException invalid => invalid.Message,

        _ => ex.Message,
    };

    private static string DescribeHttp(HttpRequestException ex) => ex.StatusCode switch
    {
        null =>
            "The server could not be reached. Check that it is running and that you are online.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "This deployment does not allow that action. Management actions are disabled or the API key is wrong.",
        HttpStatusCode.NotFound =>
            "That endpoint is not available on this deployment.",
        HttpStatusCode.Conflict =>
            "A scan is already running. Wait for it to finish and try again.",
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
            "The server is temporarily unavailable — most likely restarting or waking from idle. Try again shortly.",
        var code when (int)code >= 500 =>
            $"The server hit an error ({(int)code}). Check the server logs for details.",
        var code =>
            $"The server rejected the request ({(int)code} {code}).",
    };
}
