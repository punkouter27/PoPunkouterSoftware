using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Typed API client for all <c>/api/*</c> endpoints consumed by the Blazor WASM UI.
/// Uses <see cref="AppJsonContext"/> for trim-safe JSON serialization and
/// <see cref="ProblemDetailsParser"/> for structured error messages.
/// </summary>
public sealed class PortfolioApiClient
{
    private readonly HttpClient _http;

    public PortfolioApiClient(HttpClient http) => _http = http;

    public Task<ConfigResponse?> GetConfigAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/config", AppJsonContext.Default.ConfigResponse, ct);

    public Task<PortfolioResponse?> GetPortfolioAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/portfolio", AppJsonContext.Default.PortfolioResponse, ct);

    public Task<OpsSummary?> GetOpsSummaryAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary, ct);

    public Task<AzureReport?> GetAzureReportAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/diag/report", AppJsonContext.Default.AzureReport, ct);

    public Task<SignInReport?> GetSignInsAsync(int days, CancellationToken ct = default) =>
        _http.GetFromJsonAsync($"/api/signins?days={days}", AppJsonContext.Default.SignInReport, ct);

    public Task<HttpResponseMessage> StartRefreshAsync(CancellationToken ct = default) =>
        _http.PostAsync("/api/diag/refresh", content: null, ct);

    public Task<HttpResponseMessage> CancelRefreshAsync(CancellationToken ct = default) =>
        _http.PostAsync("/api/diag/cancel-refresh", content: null, ct);

    public Task<HttpResponseMessage> RestartSiteAsync(string name, CancellationToken ct = default) =>
        _http.PostAsync($"/api/diag/restart/{Uri.EscapeDataString(name)}", content: null, ct);

    public async Task<AiTriageAdHocResponse> RewriteAiSummaryAsync(IReadOnlyList<string> attentionItems, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(
            new AiTriageAdHocRequest(attentionItems),
            AppJsonContext.Default.AiTriageAdHocRequest);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/diag/ai", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await ProblemDetailsParser.TryReadDetailAsync(resp, ct);
            throw new HttpRequestException(detail ?? $"Rewrite failed ({(int)resp.StatusCode}).");
        }

        return await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.AiTriageAdHocResponse, ct)
            ?? new AiTriageAdHocResponse(false, null);
    }
}
