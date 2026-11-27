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

    public Task<DiagReportDto?> GetDiagAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/diag", AppJsonContext.Default.DiagReportDto, ct);

    public Task<AzureReport?> GetAzureReportAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/diag/report", AppJsonContext.Default.AzureReport, ct);

    public Task<SignInReport?> GetSignInsAsync(int days, CancellationToken ct = default) =>
        _http.GetFromJsonAsync($"/api/signins?days={days}", AppJsonContext.Default.SignInReport, ct);

    // ─── GLOBAL snapshot (signed-in only) ────────────────────────────────────
    // Every one of these reads a committed crawl of the owner's workspace; the API answers
    // 401 to an anonymous caller, so the pages carry [Authorize] like /azure and /users.

    public Task<GlobalSummaryDto?> GetGlobalSummaryAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/summary", GlobalJsonContext.Default.GlobalSummaryDto, ct);

    public Task<GlobalFleetDto?> GetGlobalFleetAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/fleet", GlobalJsonContext.Default.GlobalFleetDto, ct);

    public Task<GlobalVaultDto?> GetGlobalVaultAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/vault", GlobalJsonContext.Default.GlobalVaultDto, ct);

    public Task<GlobalLessonsFileDto?> GetGlobalLessonsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/lessons", GlobalJsonContext.Default.GlobalLessonsFileDto, ct);

    public Task<GlobalReadinessDto?> GetGlobalReadinessAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/readiness", GlobalJsonContext.Default.GlobalReadinessDto, ct);

    public Task<GlobalBriefingDto?> GetGlobalBriefingAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/briefing", GlobalJsonContext.Default.GlobalBriefingDto, ct);

    public Task<GlobalRlDto?> GetGlobalRlAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/rl", GlobalJsonContext.Default.GlobalRlDto, ct);

    public Task<GlobalQueueDto?> GetGlobalQueueAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/queue", GlobalJsonContext.Default.GlobalQueueDto, ct);

    public Task<GlobalUniteDto?> GetGlobalUniteAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync("/api/global/unite", GlobalJsonContext.Default.GlobalUniteDto, ct);

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
