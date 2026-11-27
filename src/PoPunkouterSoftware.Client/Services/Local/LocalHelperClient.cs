using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>Where the browser stands with the helper running on the owner's PC.</summary>
public enum LocalHelperState
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>Nothing answers on 127.0.0.1:5050: the helper is not running, or this is not the PC.</summary>
    Offline,

    /// <summary>The helper answers but this browser has no (or a wrong) token.</summary>
    NeedsToken,

    /// <summary>Token accepted: the buttons that act on the PC work.</summary>
    Connected,
}

/// <summary>
/// Talks to <c>scripts/global/local_helper.py</c> on <c>http://127.0.0.1:5050</c>.
/// </summary>
/// <remarks>
/// The site runs in Azure and cannot reach the owner's PC, so everything that must happen
/// there (open a repo in VS Code, launch the GPU queue, send a job to the cloud GPU, refresh
/// the snapshot) goes from the browser straight to a helper on the same machine. The helper
/// only accepts this site's origin and a token the owner pastes once; the token lives in this
/// browser's localStorage and is never sent to the site. Away from the PC the helper is simply
/// <see cref="LocalHelperState.Offline"/> and the buttons are not shown.
/// </remarks>
public sealed class LocalHelperClient(IJSRuntime js) : IDisposable
{
    public const string BaseUrl = "http://127.0.0.1:5050";
    public const string TokenStorageKey = "po.localHelper.token";
    public const string TokenHeader = "X-Po-Helper-Token";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(4) };
    private string? _token;
    private bool _tokenLoaded;

    public LocalHelperState State { get; private set; } = LocalHelperState.Unknown;

    /// <summary>The workspace folder the helper reported, once connected.</summary>
    public string? Workspace { get; private set; }

    public bool IsConnected => State == LocalHelperState.Connected;

    public async Task<LocalHelperState> ConnectAsync(CancellationToken ct = default)
    {
        await LoadTokenAsync();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
            AddToken(request);
            using var response = await _http.SendAsync(request, timeout.Token);
            var reply = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(GlobalJsonContext.Default.LocalHelperResult, timeout.Token)
                : null;
            Workspace = reply is { Authorized: true } ? reply.Output : null;
            State = reply switch
            {
                { Ok: true, Authorized: true } => LocalHelperState.Connected,
                { Ok: true } => LocalHelperState.NeedsToken,
                _ => LocalHelperState.Offline,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            State = LocalHelperState.Offline;
        }

        return State;
    }

    public async Task<LocalHelperState> SaveTokenAsync(string token, CancellationToken ct = default)
    {
        _token = token.Trim();
        _tokenLoaded = true;
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", ct, TokenStorageKey, _token);
        }
        catch (JSException)
        {
            // localStorage blocked: the token still works until this tab closes.
        }

        return await ConnectAsync(ct);
    }

    public async Task<LocalHelperResult> SendAsync(LocalHelperRequest request, CancellationToken ct = default)
    {
        await LoadTokenAsync();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/action")
            {
                Content = JsonContent.Create(request, GlobalJsonContext.Default.LocalHelperRequest),
            };
            AddToken(message);
            using var response = await _http.SendAsync(message, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                State = LocalHelperState.NeedsToken;
                return new LocalHelperResult { Output = "This PC's helper did not accept the token. Paste it again." };
            }

            var reply = await response.Content.ReadFromJsonAsync(GlobalJsonContext.Default.LocalHelperResult, ct);
            return reply ?? new LocalHelperResult { Output = $"The helper answered {(int)response.StatusCode} with no body." };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            State = LocalHelperState.Offline;
            return new LocalHelperResult
            {
                Output = "The helper on this PC is not answering. Start scripts\\global\\start_local_helper.bat and try again.",
            };
        }
    }

    private async Task LoadTokenAsync()
    {
        if (_tokenLoaded)
            return;

        _tokenLoaded = true;
        try
        {
            _token = await js.InvokeAsync<string?>("localStorage.getItem", TokenStorageKey);
        }
        catch (JSException)
        {
            _token = null;
        }
    }

    private void AddToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_token))
            request.Headers.TryAddWithoutValidation(TokenHeader, _token);
    }

    public void Dispose() => _http.Dispose();
}
