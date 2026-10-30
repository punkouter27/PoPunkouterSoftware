using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Writes the plain-English status paragraph that leads the <c>/azure</c> page — the
/// "what is actually going on with the site right now" narrative, generated from every
/// number the scan produced rather than from the attention-item list alone.
///
/// <para><b>Backend: Azure AI Foundry.</b> Calls the chat-completions API on the shared
/// <c>po-aiservices-shared2</c> AI Services account in the <c>poshared</c> resource group —
/// the same estate-wide shared-resource pattern as <c>kv-poshared2</c>. The default deployment
/// is <c>gpt-5.4-nano</c>, the cheapest deployment on that account that can write a coherent
/// paragraph. This replaces the previous Hugging Face <c>flan-t5-base</c> integration
/// wholesale; there is deliberately still exactly ONE AI client in the app.</para>
///
/// <para><b>Auth.</b> An API key when one is configured, otherwise Entra ID via the shared
/// <see cref="TokenCredential"/> singleton (System-Assigned Managed Identity in Azure,
/// <c>az login</c> locally). Keys win because that is the estate convention — every sibling
/// app carries a <c>&lt;App&gt;--AzureOpenAI--ApiKey</c> secret and this one already has its
/// own provisioned — while the Entra path is what works with no key at all, and needs the
/// <c>Cognitive Services OpenAI User</c> role on the account.</para>
///
/// <para><b>Config lives under <c>StatusNarrator</c>, not <c>AzureAi</c>.</b> The shared vault
/// is loaded wholesale into configuration by <see cref="AppKeyVaultSecretManager"/> and holds
/// estate-wide secrets named <c>AzureAI--ApiKey</c> / <c>AzureAI--Endpoint</c> that belong to a
/// different account. Configuration keys are case-INSENSITIVE, so an <c>AzureAi</c> section
/// silently inherited that foreign key and every call came back 401. <c>StatusNarrator</c>
/// collides with nothing in the vault. The key still falls back to the app's own provisioned
/// <c>AzureOpenAI:ApiKey</c>, but the endpoint and deployment deliberately do NOT read the
/// matching vault secrets: <c>PoPunkouterSoftware--AzureOpenAI--DeploymentName</c> is stale
/// (<c>gpt-4.1-nano</c>, which 404s on this account).</para>
///
/// <para><b>AI outages must not cascade.</b> The typed <see cref="HttpClientName"/> client has
/// NO resilience pipeline (mirroring <c>health</c> / <c>azure-probe</c>): a model outage
/// degrades to the rule-based narrative in <see cref="BuildNarrativeFallback"/>, never to a
/// dead "unavailable" state and never to a retry storm.</para>
/// </summary>
public sealed class AiTriageService
{
    public const string HttpClientName = "ai-azure";
    public const string DefaultEndpoint = "https://po-aiservices-shared2.cognitiveservices.azure.com/";

    /// <summary>Cheapest deployment on the shared account that writes a coherent paragraph. Verified live.</summary>
    public const string DefaultDeployment = "gpt-5.4-nano";

    /// <summary>
    /// Pinned GA data-plane version. Confirmed against the live account before this
    /// integration was written; the newer <c>max_completion_tokens</c> parameter name is
    /// required here (the models on this account reject the legacy <c>max_tokens</c>).
    /// </summary>
    public const string DefaultApiVersion = "2024-10-21";

    /// <summary>Entra scope for the Cognitive Services data plane.</summary>
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AiTriageService> _log;
    private readonly TokenCredential? _credential;

    public AiTriageService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<AiTriageService> log,
        TokenCredential? credential = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _log = log;
        _credential = credential;
    }

    /// <summary>
    /// On by default: the model runs on the owner's own Azure AI Services account, so there
    /// is no free-tier quota to protect and a dashboard with no narrative is the worse
    /// default. Set <c>FeatureFlags:EnableAiSummary=false</c> to fall back to the rule-based
    /// narrative without removing the integration.
    /// </summary>
    public bool IsEnabled => _config.GetValue("FeatureFlags:EnableAiSummary", true);

    private string Endpoint => Coalesce(_config["StatusNarrator:Endpoint"], DefaultEndpoint).TrimEnd('/');
    private string Deployment => Coalesce(_config["StatusNarrator:Deployment"], DefaultDeployment);
    private string ApiVersion => Coalesce(_config["StatusNarrator:ApiVersion"], DefaultApiVersion);

    /// <summary>
    /// The API key, preferring this feature's own setting and falling back to the app's
    /// already-provisioned <c>PoPunkouterSoftware--AzureOpenAI--ApiKey</c> so production works
    /// without adding a vault secret. Null when neither is set, which selects the Entra path.
    /// </summary>
    private string? ApiKey =>
        Coalesce(_config["StatusNarrator:ApiKey"], _config["AzureOpenAI:ApiKey"] ?? "") is { Length: > 0 } key
            ? key
            : null;

    /// <summary>
    /// Empty and whitespace count as unset. Config binding turns an omitted appsettings value
    /// into "" rather than null, so a plain <c>??</c> would treat a blank placeholder as a real
    /// setting and, in the case of the endpoint, send every request to "".
    /// </summary>
    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    // ─── Model call ──────────────────────────────────────────────────────────

    /// <summary>
    /// The ad-hoc entry point behind <c>POST /api/diag/ai</c> ("Regenerate now"). Takes only
    /// the attention-item list, so it produces a thinner paragraph than the scan-time path —
    /// that path passes full <see cref="StatusFacts"/> via <see cref="SummarizeAsync(StatusFacts, CancellationToken)"/>.
    /// </summary>
    public Task<AiTriageResult> SummarizeAsync(AiTriageRequest req, CancellationToken ct) =>
        SummarizeAsync(StatusFacts.FromAttentionItems(req.AttentionItems), ct);

    /// <summary>Generates the narrative from the full fact set. Never throws for an upstream failure.</summary>
    public async Task<AiTriageResult> SummarizeAsync(StatusFacts facts, CancellationToken ct)
    {
        if (!IsEnabled)
            return new AiTriageResult(Available: false, Summary: null, Model: null, Reason: "disabled");

        var deployment = Deployment;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"{Endpoint}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}";

            using var msg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new ChatRequest(
                    Messages:
                    [
                        new ChatMessage("system", SystemPrompt),
                        new ChatMessage("user", facts.ToPrompt()),
                    ],
                    MaxCompletionTokens: 900)),
            };
            msg.Headers.Accept.ParseAdd("application/json");

            if (!await AuthorizeAsync(msg, ct))
            {
                return new AiTriageResult(
                    Available: false, Summary: null, Model: deployment, Reason: "no-credential");
            }

            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning(
                    "Azure AI chat completions returned {Status} for deployment {Deployment} at {Endpoint} (auth: {AuthMode}): {Body}",
                    (int)resp.StatusCode, deployment, Endpoint, _authMode, Truncate(body, 400));
                return new AiTriageResult(
                    Available: false, Summary: null, Model: deployment, Reason: $"http-{(int)resp.StatusCode}");
            }

            var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                // A reasoning model that spent its whole budget before emitting any prose
                // returns an empty content string rather than an error — treat it as a miss
                // and fall back rather than showing a blank paragraph.
                _log.LogWarning("Azure AI returned an empty completion for deployment {Deployment}", deployment);
                return new AiTriageResult(
                    Available: false, Summary: null, Model: deployment, Reason: "empty-response");
            }

            return new AiTriageResult(Available: true, Summary: text, Model: deployment, Reason: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure AI narrative generation failed; falling back to the rule-based paragraph");
            return new AiTriageResult(Available: false, Summary: null, Model: deployment, Reason: "exception");
        }
    }

    /// <summary>
    /// Attaches either the configured API key or an Entra bearer token. Returns false when
    /// neither is available, so the caller reports "no-credential" instead of firing an
    /// unauthenticated request that would come back as a confusing 401.
    /// </summary>
    private async Task<bool> AuthorizeAsync(HttpRequestMessage msg, CancellationToken ct)
    {
        if (ApiKey is { } apiKey)
        {
            msg.Headers.Add("api-key", apiKey);
            _authMode = "api-key";
            return true;
        }

        if (_credential is null)
            return false;

        var token = await _credential.GetTokenAsync(new TokenRequestContext([TokenScope]), ct);
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        _authMode = "entra";
        return true;
    }

    /// <summary>
    /// Which auth path the last request used, reported in the failure log. A 401 here is
    /// almost always a configuration question — which key, which endpoint — and a warning that
    /// omits them sends the reader hunting through a shared vault of 190 secrets.
    /// </summary>
    private string _authMode = "none";

    // The text sits beside tiles that already print every number in the report. "Use the
    // actual numbers" produced ~90 words restating them (health %, 30-day cost, projection,
    // budget, three cost drivers) — 355px of a phone's first screen saying the tiles twice.
    // Bump the "narrator:N" input in ToHashInputs when this changes, or the cache keeps the old text.
    private const string SystemPrompt =
        "You are the status narrator for a personal Azure web-app portfolio dashboard. " +
        "Your text sits beside tiles that already show the health percentage, the 30-day cost, " +
        "the resource count, the month-end forecast against budget, the top cost drivers and " +
        "the slowest response times. Write at most TWO short sentences, under 40 words in " +
        "total, in plain English: what is wrong right now and the single most useful next step, " +
        "or a one-sentence all-clear. Lead with anything offline, then security, then cost. " +
        "Name specific apps when they are named in the data. Do not repeat numbers the tiles " +
        "already show, and never invent anything that is not in the data. No bullet points, " +
        "no headings, no markdown, no preamble - return only the text.";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    // ─── Scan-time orchestration ─────────────────────────────────────────────

    /// <summary>
    /// The scan-time entry point (called by <c>ReportRefreshRunner</c>): decides whether the
    /// model actually needs to run this scan, and always returns a populated
    /// <see cref="AiSummaryResult"/> — the dashboard never sees a dead state.
    ///
    /// <para>Skips the model call (Source <c>"cached"</c>) when <paramref name="previous"/> was
    /// itself a real generation (<c>Source == "ai"</c>) and the fact hash is unchanged since
    /// that run. Any other case (first scan, facts changed, or the previous summary was itself
    /// a fallback) calls the model for real.</para>
    /// </summary>
    public async Task<AiSummaryResult> GenerateSummaryAsync(
        StatusFacts facts, AiSummaryResult? previous, CancellationToken ct)
    {
        var hash = ComputeAttentionHash(facts.ToHashInputs());
        var now = DateTimeOffset.UtcNow;

        if (previous is { Source: "ai" } && previous.AttentionHash == hash)
            return previous with { Source = "cached", GeneratedAtUtc = now };

        if (!IsEnabled)
        {
            return new AiSummaryResult
            {
                Text = BuildNarrativeFallback(facts),
                Source = "disabled",
                Model = null,
                GeneratedAtUtc = now,
                AttentionHash = hash,
            };
        }

        var result = await SummarizeAsync(facts, ct);
        if (result.Available && !string.IsNullOrWhiteSpace(result.Summary))
        {
            return new AiSummaryResult
            {
                Text = result.Summary!,
                Source = "ai",
                Model = result.Model,
                GeneratedAtUtc = now,
                AttentionHash = hash,
            };
        }

        return new AiSummaryResult
        {
            Text = BuildNarrativeFallback(facts),
            Source = "template-fallback",
            Model = result.Model,
            GeneratedAtUtc = now,
            AttentionHash = hash,
        };
    }

    /// <summary>
    /// Attention-items-only overload, kept for callers that have nothing richer to hand.
    /// Delegates to the fact-based path via <see cref="StatusFacts.FromAttentionItems"/>, so
    /// the caching and fallback semantics are identical.
    /// </summary>
    public Task<AiSummaryResult> GenerateSummaryAsync(
        IReadOnlyList<string>? attentionItems, AiSummaryResult? previous, CancellationToken ct) =>
        GenerateSummaryAsync(StatusFacts.FromAttentionItems(attentionItems), previous, ct);

    // ─── Rule-based fallbacks ────────────────────────────────────────────────

    /// <summary>
    /// Pure, no-I/O narrative built from the full fact set — what the page shows whenever the
    /// model is off or unreachable. Unlike <see cref="BuildTemplateFallback"/> this reads as a
    /// status paragraph rather than a diagnostic note, because it occupies the same
    /// lead-paragraph slot the model output would.
    /// </summary>
    public static string BuildNarrativeFallback(StatusFacts facts)
    {
        var sentences = new List<string>();

        if (facts.BrokenServices > 0)
        {
            var named = facts.BrokenServiceNames.Take(3).ToList();
            var subject = named.Count > 0 ? JoinNames(named) : (facts.BrokenServices == 1 ? "1 service" : $"{facts.BrokenServices} services");
            var extra = facts.BrokenServices > named.Count && named.Count > 0
                ? $" (and {facts.BrokenServices - named.Count} more)"
                : "";
            // No "N of M apps healthy (P%)": the health tile beside this text says exactly that.
            sentences.Add($"{subject}{extra} {(facts.BrokenServices == 1 ? "is" : "are")} currently unavailable.");
        }
        else if (facts.TotalServices > 0)
        {
            sentences.Add($"All {facts.TotalServices} apps are responding normally.");
        }

        var problems = new List<string>();
        if (facts.SecurityFindings > 0)
            problems.Add($"{facts.SecurityFindings} security configuration finding{(facts.SecurityFindings == 1 ? "" : "s")}");
        if (facts.CleanupCandidates > 0)
            problems.Add($"{facts.CleanupCandidates} resource{(facts.CleanupCandidates == 1 ? "" : "s")} worth cleaning up");
        if (problems.Count > 0)
            sentences.Add($"There {(problems.Count == 1 ? "is" : "are")} also {JoinNames(problems)}.");

        // Spend is on the cost tile and the forecast card; it earns a sentence only when it is news.
        if (facts.BudgetUsd is > 0 and var budget && facts.ProjectedMonthCost > budget)
            sentences.Add($"This month is on pace to overrun the ${budget:F0} budget.");

        if (facts.IsStale)
            sentences.Add("This data is stale — run a refresh for the current picture.");

        return sentences.Count > 0
            ? string.Join(" ", sentences)
            : "No Azure scan data is available yet. Run a refresh to generate the first report.";
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    /// <summary>
    /// Pure, no-I/O rule-based one-sentence summary built directly from the attention items
    /// list. Retained for the ad-hoc <c>POST /api/diag/ai</c> path, which only ever has the
    /// flat string list. Parses the fixed formats <see cref="AttentionItemsBuilder"/> produces
    /// ("{name} is unavailable", "{n} security configuration finding(s)", "{n} cleanup
    /// candidate(s)", the staleness note) rather than requiring structured counts.
    /// </summary>
    public static string BuildTemplateFallback(IReadOnlyList<string>? attentionItems)
    {
        var items = attentionItems ?? Array.Empty<string>();
        if (items.Count == 0)
            return "No issues detected. No AI summary available.";

        var unavailableCount = items.Count(i => i.EndsWith(" is unavailable", StringComparison.Ordinal));
        var securityCount = ExtractLeadingCount(items, "security configuration finding");
        var cleanupCount = ExtractLeadingCount(items, "cleanup candidate");
        var isStale = items.Any(i => i.Contains("stale", StringComparison.OrdinalIgnoreCase));

        var parts = new List<string>();
        if (securityCount > 0)
            parts.Add($"{securityCount} security finding(s)");
        if (cleanupCount > 0)
            parts.Add($"{cleanupCount} cleanup candidate(s)");
        if (unavailableCount > 0)
            parts.Add($"{unavailableCount} service(s) unavailable");

        var body = parts.Count > 0
            ? string.Join(", ", parts) + "."
            : "Attention items present but not individually categorized.";
        var staleNote = isStale ? " Azure data may be stale." : "";

        return $"{body}{staleNote} No AI summary available.";
    }

    private static int ExtractLeadingCount(IReadOnlyList<string> items, string marker)
    {
        var match = items.FirstOrDefault(i => i.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return 0;

        var firstToken = match.Split(' ', 2)[0];
        return int.TryParse(firstToken, out var n) ? n : 0;
    }

    /// <summary>
    /// Pure, no-I/O SHA-256 hex digest of a string list, joined with a control character
    /// (U+001F) unlikely to appear in generated text so items containing commas, pipes, etc.
    /// cannot collide two different lists onto the same hash.
    /// </summary>
    public static string ComputeAttentionHash(IReadOnlyList<string>? attentionItems)
    {
        const char separator = (char)0x1F; // ASCII "unit separator" -- unlikely to appear in generated text
        var joined = string.Join(separator, attentionItems ?? Array.Empty<string>());
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ─── Azure OpenAI wire shapes ────────────────────────────────────────────

    private sealed record ChatRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}

/// <summary>
/// Everything the narrator is allowed to know about the current scan. Built once per
/// generation so the model writes from the real numbers rather than from a bare list of
/// attention strings — "using all the underlying data as the data source".
///
/// GoF: Value Object — immutable data carrier; <see cref="ToPrompt"/> and
/// <see cref="ToHashInputs"/> are pure projections of its own fields.
/// </summary>
public sealed record StatusFacts
{
    public string SubscriptionName { get; init; } = "Azure";
    public DateTime? GeneratedAt { get; init; }
    public bool IsStale { get; init; }

    public int TotalServices { get; init; }
    public int ActiveServices { get; init; }
    public int BrokenServices { get; init; }
    public int HealthPercent { get; init; }
    public int TotalResources { get; init; }

    public double Cost30Days { get; init; }
    public double ProjectedMonthCost { get; init; }
    public double? BudgetUsd { get; init; }

    public int SecurityFindings { get; init; }
    public int CleanupCandidates { get; init; }
    public double AvgResponseTimeMs { get; init; }

    public IReadOnlyList<string> BrokenServiceNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AttentionItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RecentChanges { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TopCostDrivers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SlowestEndpoints { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Minimal facts for callers that only have the attention-item list (the ad-hoc
    /// <c>POST /api/diag/ai</c> path). The narrative is thinner, but the code path is the same.
    /// </summary>
    public static StatusFacts FromAttentionItems(IReadOnlyList<string>? attentionItems) =>
        new() { AttentionItems = attentionItems ?? Array.Empty<string>() };

    /// <summary>The user-message body: a compact labelled block, not JSON — cheaper in tokens and easier for a small model to follow.</summary>
    public string ToPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Subscription: {SubscriptionName}");
        if (GeneratedAt is { } at)
            sb.AppendLine($"Scan time (UTC): {at:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Data is stale: {(IsStale ? "yes" : "no")}");

        if (TotalServices > 0)
        {
            sb.AppendLine($"Apps: {TotalServices} total, {ActiveServices} healthy, {BrokenServices} unavailable ({HealthPercent}% healthy)");
            if (BrokenServiceNames.Count > 0)
                sb.AppendLine($"Unavailable apps: {string.Join(", ", BrokenServiceNames)}");
        }

        sb.AppendLine($"Azure resources: {TotalResources}");
        if (Cost30Days > 0)
            sb.AppendLine($"Cost: ${Cost30Days:F2} over 30 days, projected ${ProjectedMonthCost:F2} for the month" +
                          (BudgetUsd is > 0 ? $", budget ${BudgetUsd:F2}" : ""));
        if (TopCostDrivers.Count > 0)
            sb.AppendLine($"Top cost drivers: {string.Join("; ", TopCostDrivers)}");

        sb.AppendLine($"Security findings: {SecurityFindings}");
        sb.AppendLine($"Cleanup candidates: {CleanupCandidates}");
        if (AvgResponseTimeMs > 0)
            sb.AppendLine($"Average response time: {AvgResponseTimeMs:F0} ms");
        if (SlowestEndpoints.Count > 0)
            sb.AppendLine($"Slowest endpoints: {string.Join("; ", SlowestEndpoints)}");
        if (RecentChanges.Count > 0)
            sb.AppendLine($"Changed since the previous scan: {string.Join("; ", RecentChanges)}");
        if (AttentionItems.Count > 0)
            sb.AppendLine($"Attention items: {string.Join("; ", AttentionItems)}");

        return sb.ToString();
    }

    /// <summary>
    /// The values that make a regeneration worthwhile. Deliberately excludes the scan
    /// timestamp and the raw cost cents — otherwise every single scan would look "changed"
    /// and the cache would never hit. Cost is bucketed to whole dollars for the same reason.
    /// </summary>
    public IReadOnlyList<string> ToHashInputs()
    {
        var inputs = new List<string>
        {
            // The prompt's version: a reworded prompt must invalidate text cached under the old one.
            "narrator:2",
            $"health:{HealthPercent}",
            $"broken:{BrokenServices}",
            $"total:{TotalServices}",
            $"resources:{TotalResources}",
            $"security:{SecurityFindings}",
            $"cleanup:{CleanupCandidates}",
            $"cost:{Math.Round(Cost30Days)}",
            $"stale:{IsStale}",
        };
        inputs.AddRange(BrokenServiceNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => $"down:{n}"));
        inputs.AddRange(AttentionItems);
        inputs.AddRange(RecentChanges);
        return inputs;
    }
}

public sealed record AiTriageRequest(
    [property: JsonPropertyName("attentionItems")] IReadOnlyList<string>? AttentionItems);

public sealed record AiTriageResult(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("reason")] string? Reason);
