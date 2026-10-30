using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Pushes a phone notification through <see href="https://ntfy.sh">ntfy</see> — one JSON POST,
/// no account, no SDK. Nothing in this app told anyone when something broke: the nightly scan
/// ran red for a week and an outage was only visible to someone already looking at /azure.
///
/// <para>Off unless <c>Alerts:NtfyTopic</c> is set (Key Vault secret
/// <c>PoPunkouterSoftware--Alerts--NtfyTopic</c>). On the public ntfy.sh server the topic name
/// IS the credential — anyone who knows it can read the feed — so it is a secret, not config.
/// <c>Alerts:NtfyToken</c> is optional, for a self-hosted server with access control.</para>
///
/// <para>Best-effort by design: a failed notification is logged and dropped, never thrown,
/// because every caller is a background loop whose real job must not stop for it.</para>
/// </summary>
public sealed class AlertNotifier(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<AlertNotifier> logger)
{
    public const string HttpClientName = "alerts";

    public bool Enabled => !string.IsNullOrWhiteSpace(config["Alerts:NtfyTopic"]);

    /// <summary>Public base URL of this site, for the tap-through link on each notification.</summary>
    public string SiteUrl => (config["Alerts:SiteUrl"] ?? "").TrimEnd('/');

    /// <param name="priority">ntfy's 1 (min) – 5 (urgent).</param>
    /// <param name="clickPath">Site-relative path opened when the notification is tapped.</param>
    public async Task SendAsync(
        string title, string message, int priority = 3, string? clickPath = null,
        string[]? tags = null, CancellationToken ct = default)
    {
        var topic = config["Alerts:NtfyTopic"];
        if (string.IsNullOrWhiteSpace(topic))
            return;

        var server = (config["Alerts:NtfyServer"] is { Length: > 0 } s ? s : "https://ntfy.sh").TrimEnd('/');
        try
        {
            // JSON publishing (POST to the server root) rather than header publishing: titles
            // carry app names and dashes, and HTTP header values are not safe for arbitrary text.
            using var req = new HttpRequestMessage(HttpMethod.Post, server + "/")
            {
                Content = JsonContent.Create(new
                {
                    topic,
                    title,
                    message,
                    priority,
                    tags = tags ?? [],
                    click = clickPath is null || SiteUrl.Length == 0 ? null : SiteUrl + clickPath,
                }),
            };
            if (config["Alerts:NtfyToken"] is { Length: > 0 } token)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await httpClientFactory.CreateClient(HttpClientName).SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Alert '{Title}' was rejected by ntfy: HTTP {Status}", title, (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Alert '{Title}' could not be sent", title);
        }
    }
}
