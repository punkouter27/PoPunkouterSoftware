using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared;
using Radzen;
using Radzen.Blazor;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Client;

public partial class AzureDashboard
{
    private AzureReport? report;
    private OpsSummary? _summary;
    private List<SafeToRemoveItem> safeToRemove = new();
    private bool _loading = true;
    private string? _loadError;
    private bool _advancedLoading;
    private string? _advancedError;

    // ── Drawers (AppDrawer.razor) ───────────────────────────────────────────────
    private AppDrawer? _diagnosticsDrawer;
    private AppDrawer? _appDrawer;
    private UptimeRow? _selectedRow;

    // The app drawer's reach and own-group cost. /api/signins is a live Log Analytics query
    // (rate-limited, see Host/RateLimits.cs), so it is fetched once per visit, on the first
    // drawer open — most visits never open one — and never on first paint.
    private SignInReport? _signIns;
    private bool _signInsRequested;

    // ── Status narrative (see AzureStatusNarrative.razor) ───────────────────────
    // The persisted per-scan summary comes from whichever of the two read contracts is
    // freshest in view: the full report once advanced diagnostics has been opened, otherwise
    // the compact first-paint summary — both project the identical AzureReport.AiSummary
    // field, so this never disagrees with what the server most recently computed.
    private AiSummaryResult? CurrentAiSummary => report?.AiSummary ?? _summary?.AiSummary;

    // ── Memoized analysis model ────────────────────────────────────────────────
    // These were previously expression-bodied properties that re-ran the full
    // GroupBy/median/sort pipeline on EVERY property access — and the markup reads
    // them several times per render, with a render per SignalR progress tick. On the
    // single WASM thread that meant ~10 full model rebuilds per progress update.
    // They are now computed exactly once per report load (RebuildDerivedState) and
    // once per filter change (ApplyResourceFilter).
    private List<ConsolidatedService> _consolidatedServices = new();
    private List<PriorityQueueItem> _priorityQueue = new();
    private List<ResourceExplorerItem> _resourceExplorerItems = new();
    private List<ResourceExplorerItem> _filteredResourceExplorerItems = new();

    private List<PriorityQueueItem> PriorityQueue => _priorityQueue;
    private List<ResourceExplorerItem> ResourceExplorerItems => _resourceExplorerItems;
    private List<ResourceExplorerItem> FilteredResourceExplorerItems => _filteredResourceExplorerItems;

    // Opens on the flagged rows only: "All" was 58 rows of storage accounts, plans and
    // healthy sites around a handful worth reading. Per-concern chips went with it — the
    // Concern column already names each row's reason, and the flagged list is short.
    private const string FlaggedView = "Needs a look";
    private static readonly string[] ResourceViews = [FlaggedView, "All"];
    private string _resourceView = FlaggedView;

    private void RebuildDerivedState()
    {
        _consolidatedServices = DashboardDerivations.BuildConsolidatedServices(report);
        _priorityQueue = DashboardDerivations.BuildPriorityQueue(report, _consolidatedServices, safeToRemove);
        _resourceExplorerItems = DashboardDerivations.BuildResourceExplorerItems(report, _consolidatedServices, safeToRemove);
        ApplyResourceFilter();
    }

    private void SetResourceView(string view)
    {
        _resourceView = view;
        ApplyResourceFilter();
    }

    private void ApplyResourceFilter() =>
        _filteredResourceExplorerItems = _resourceExplorerItems
            .Where(item => _resourceView != FlaggedView || item.Risk != "None")
            .ToList();

    private bool _refreshing;

    // The live progress strip, addressed directly so a hub tick does not re-render the page.
    // See AzureRefreshProgress.razor and the RefreshProgress handler in EnsureHubConnectedAsync.
    private AzureRefreshProgress? _progressUi;

    private int _progressPercent;
    private string _progressStep = "";
    private bool _refreshFailed;
    private string? _refreshFailureMessage;
    private bool _userCancelled;
    private CancellationTokenSource? _refreshCts;
    private const int RefreshTimeoutSeconds = 120;

    // Whether the server allows management actions (POST /refresh etc.). In Production the
    // ManagementActionFilter rejects them with 403 unless explicitly enabled — showing an
    // active Refresh button that can only ever fail is a lie, so it is hidden instead.
    private bool _managementActionsEnabled = true;

    private async Task DownloadReportAsync()
    {
        if (report is null)
            await LoadReportAsync();
        if (report is null)
            return;

        var json = JsonSerializer.Serialize(report, AppJsonContext.Default.AzureReport);
        var timestamp = (report.GeneratedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd_HHmm");
        var filename = $"azure-report-{timestamp}.json";

        await JS.InvokeVoidAsync("downloadTextFile", filename, json, "application/json");
    }

    private async Task CopyText(string text)
    {
        bool copied;
        try
        {
            copied = await JS.InvokeAsync<bool>("copyToClipboard", text);
        }
        catch (JSException)
        {
            copied = false;
        }

        if (copied)
            NotificationService.Notify(NotificationSeverity.Success, "Copied", "Azure CLI command copied to the clipboard.");
        else
            NotificationService.Notify(NotificationSeverity.Error, "Copy failed", "The clipboard is unavailable — copy the command manually.");
    }

    private async Task RestartSiteAsync(PriorityQueueItem item)
    {
        try
        {
            var resp = await Http.PostAsync($"/api/diag/restart/{Uri.EscapeDataString(item.Item)}", null);
            if (resp.IsSuccessStatusCode)
                NotificationService.Notify(NotificationSeverity.Success, "Restart sent",
                    $"{item.Item} is restarting. Rescan in a minute to see whether it came back.");
            else
                NotificationService.Notify(NotificationSeverity.Error, "Restart failed",
                    await ReadProblemDetailAsync(resp) ?? $"The server rejected the restart ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Restart failed", ex.Message);
        }
    }

    // ── Deep link: /azure?focus=<service> ──────────────────────────────────────
    // What an outage notification links to (ServicePingerService.NotifyTransitionsAsync): the
    // named service's uptime row is highlighted and scrolled into view once, on the first
    // render that has a summary to show it in.
    [SupplyParameterFromQuery] public string? Focus { get; set; }
    private bool _focusRevealed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusRevealed || _summary is null || string.IsNullOrWhiteSpace(Focus))
            return;

        _focusRevealed = true;
        await JS.InvokeVoidAsync("appRevealFocus", ".azure-uptime__label.is-focused");
    }

    // ── SignalR hub connection ─────────────────────────────────────────────────
    private HubConnection? _hub;

    protected override async Task OnInitializedAsync()
    {
        await LoadConfigAsync();
        await LoadSummaryAsync();

        // A scan started before this visit (or before the visitor navigated away and back):
        // show its progress and its Cancel button rather than a Rescan that can only 409.
        if (_summary?.RefreshInProgress == true && _managementActionsEnabled)
            await RunRefreshAsync(start: false);
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cfg = await Http.GetFromJsonAsync("/api/config", AppJsonContext.Default.ConfigResponse, cts.Token);
            _managementActionsEnabled = cfg?.ManagementActionsEnabled ?? false;
        }
        catch
        {
            // Non-fatal: keep the button visible; a rejected POST is still handled gracefully.
        }
    }

    private async Task LoadSummaryAsync()
    {
        _loading = true;
        _loadError = null;
        try
        {
            // Assign through a local so a failed parse cannot half-replace the live summary.
            var loaded = await Http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary)
                ?? throw new InvalidOperationException("The Azure status endpoint returned no data.");
            _summary = loaded;
        }
        catch (Exception ex)
        {
            // _summary is deliberately LEFT ALONE. This method runs on every rescan, so
            // nulling it meant one failed post-scan reload deleted the hero, all six panes and
            // the open advanced tree and replaced them with a banner — throwing away data still
            // held in memory. A reload that fails is a stale dashboard, not an empty one; the
            // markup renders the banner above whatever is still there. Same rule Index.razor
            // already follows for the catalog.
            _loadError = FriendlyError.Describe(ex);
            Console.Error.WriteLine($"Azure summary load error: {ex}");
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>Opens the drawer first, then loads into it: the visitor sees the progress bar
    /// where the explorer will appear, not a button that does nothing for a second.</summary>
    private async Task OpenDiagnosticsAsync()
    {
        if (_diagnosticsDrawer is not null)
            await _diagnosticsDrawer.OpenAsync();
        if (report is null && !_advancedLoading)
            await LoadReportAsync();
    }

    private async Task OpenAppAsync(UptimeRow row)
    {
        _selectedRow = row;
        StateHasChanged();
        await Task.Yield(); // let the drawer's content render before it slides in
        if (_appDrawer is not null)
            await _appDrawer.OpenAsync();

        if (_signInsRequested)
            return;
        _signInsRequested = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _signIns = await Http.GetFromJsonAsync("/api/signins?days=30", AppJsonContext.Default.SignInReport, cts.Token);
        }
        catch (Exception ex)
        {
            // Decorative to this page: a 429 or a timeout says "Unavailable" in one row of
            // the drawer, never a banner over the dashboard.
            Console.Error.WriteLine($"Sign-in reach load error: {ex.Message}");
        }
        _signIns ??= new SignInReport { Available = false };
        StateHasChanged();
    }

    private async Task LoadReportAsync()
    {
        _advancedLoading = true;
        _advancedError = null;
        StateHasChanged();

        try
        {
            var loaded = await Http.GetFromJsonAsync("/api/diag/report", AppJsonContext.Default.AzureReport)
                ?? throw new InvalidOperationException("The Azure report endpoint returned no data.");
            report = loaded;

            safeToRemove = DashboardDerivations.BuildSafeToRemove(loaded);
            RebuildDerivedState();
        }
        catch (Exception ex)
        {
            // The previously loaded report, its derived queue and its explorer rows are left
            // in place for the same reason LoadSummaryAsync leaves _summary alone: this runs
            // again after every rescan, and a failed reload that wipes the tree the visitor was
            // reading is strictly worse than one that leaves it standing under a banner.
            _advancedError = FriendlyError.Describe(ex);
            Console.Error.WriteLine($"Azure dashboard load error: {ex}");
        }
        finally
        {
            _advancedLoading = false;
            StateHasChanged();
        }
    }

    private Task RefreshAsync() => RunRefreshAsync(start: true);

    /// <param name="start">False joins a scan the server is already running.</param>
    private async Task RunRefreshAsync(bool start)
    {
        _refreshing = true;
        _refreshFailed = false;
        _refreshFailureMessage = null;
        _userCancelled = false;
        _progressPercent = 0;
        _progressStep = start ? "Starting…" : "Scan already running…";
        // Keep the previous report in view during the scan — do not null it here.
        // The report will be replaced once the scan completes and LoadReportAsync is called again.
        _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(RefreshTimeoutSeconds));
        StateHasChanged();

        // A full ARM scan takes about thirty seconds, which is exactly the duration after
        // which someone switches tabs. The drone (and the chord that resolves it below) is
        // how the outcome reaches a visitor who is no longer looking at the page. Silent
        // unless they turned sound on — audioKit gates every one of these calls itself.
        await SfxAsync("audioKit.refreshStart");

        await EnsureHubConnectedAsync();

        try
        {
            // 409 means a scan is already running (another tab, the nightly workflow, a
            // visit-triggered auto-scan): follow that one rather than turn the visitor away.
            using var resp = start ? await Http.PostAsync("/api/diag/refresh", null) : null;
            if (resp is { IsSuccessStatusCode: false } && resp.StatusCode != System.Net.HttpStatusCode.Conflict)
            {
                // 403 (management actions disabled), 401 (bad key), 5xx — previously only 409
                // was handled, so these fell into the polling loop and were misreported as a
                // 120-second "timeout" every single time.
                var detail = await ReadProblemDetailAsync(resp);
                NotificationService.Notify(NotificationSeverity.Error, "Refresh rejected",
                    detail ?? $"The server rejected the refresh request ({(int)resp.StatusCode}).");
                await SfxAsync("audioKit.refreshEnd", "failure");
                _refreshing = false;
                StateHasChanged();
                return;
            }

            if (_hub?.State == HubConnectionState.Connected)
            {
                await WaitForRefreshCompletionAsync(_refreshCts!.Token);
            }
            else
            {
                // No percent values will arrive, which is why the bar above renders
                // indeterminate. The drone deepens its tremolo to say the same thing rather
                // than sweeping its filter toward a completion it cannot actually observe.
                await SfxAsync("audioKit.refreshIndeterminate");
                await WaitForRefreshCompletionAsync(_refreshCts!.Token, delayMs: 1500);
            }

            // The user-cancelled race: when Cancel lands between the wait's last poll and
            // the next yield, the wait exits cleanly (no OperationCanceledException) and the
            // original code fell into LoadSummaryAsync, overwriting `_summary` with the new
            // report the scan wrote after the user said "stop". _userCancelled is set by
            // CancelRefreshAsync and is NOT reset by the wait, so checking it here honours
            // the user's intent regardless of which path the wait took to get out.
            if (!_userCancelled)
            {
                // Captured before the reload replaces _summary: red → all green is the one
                // outcome that gets its own flourish (a "recovered" cadence and a particle bloom).
                var brokenBefore = _summary?.BrokenServices ?? 0;
                await LoadSummaryAsync();
                if (report is not null)
                    await LoadReportAsync();
                if (_refreshFailed)
                {
                    NotificationService.Notify(NotificationSeverity.Error, "Refresh failed", _refreshFailureMessage ?? "Refresh failed. Check logs for details.");
                    await SfxAsync("audioKit.refreshEnd", "failure");
                }
                else if (!_refreshCts.Token.IsCancellationRequested)
                {
                    NotificationService.Notify(NotificationSeverity.Success, "Done", "Azure report refreshed successfully.");
                    await SfxAsync("audioKit.refreshEnd",
                        brokenBefore > 0 && _summary?.BrokenServices == 0 ? "recovered" : "success");
                }
            }
        }
        catch (OperationCanceledException) when (_userCancelled)
        {
            // The user clicked Cancel — the cancellation toast was already shown; a second,
            // contradictory "Timeout" toast here would tell them it timed out instead.
        }
        catch (OperationCanceledException)
        {
            // Client-side timeout. The server scan would otherwise keep running (and keep the
            // refresh lock) for up to 10 minutes — tell it to stop instead of walking away.
            try
            { await Http.PostAsync("/api/diag/cancel-refresh", null); }
            catch { }
            NotificationService.Notify(NotificationSeverity.Warning, "Timeout",
                "Refresh took too long (120s limit); the server scan was cancelled.");
            await SfxAsync("audioKit.refreshEnd", "timeout");
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Error", ex.Message);
            await SfxAsync("audioKit.refreshEnd", "failure");
        }
        finally
        {
            _refreshing = false;
            _refreshCts?.Dispose();
            _refreshCts = null;
            StateHasChanged();
        }
    }

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage resp)
    {
        try
        {
            var problem = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(), AppJsonContext.Default.ProblemResponse);
            return string.IsNullOrWhiteSpace(problem?.Detail) ? null : problem.Detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task CancelRefreshAsync()
    {
        if (_refreshCts is not null)
        {
            _userCancelled = true;
            _refreshCts.Cancel();
            NotificationService.Notify(NotificationSeverity.Warning, "Cancelled", "Refresh operation cancelled.");
            // Here rather than in the OperationCanceledException handler: that handler exists
            // precisely because the cancellation toast has already been shown, so the sound
            // belongs on the same side of that split as the toast it accompanies.
            await SfxAsync("audioKit.refreshEnd", "cancelled");
        }
        // Signal the server to stop the in-progress scan (best-effort — swallow errors).
        try
        { await Http.PostAsync("/api/diag/cancel-refresh", null); }
        catch { }
        _refreshing = false;
        StateHasChanged();
    }

    private async Task WaitForRefreshCompletionAsync(CancellationToken ct, int delayMs = 2000)
    {
        var initialGeneratedAt = _summary?.GeneratedAt ?? report?.GeneratedAt;
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(delayMs, ct);
            if (!_refreshing)
                break;

            // Polled even with the hub up: a page that JOINED a running scan may have connected
            // after its final "done" message went out, and would otherwise wait for the timeout.
            try
            {
                var latest = await Http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary, ct);
                if (latest is not null && (latest.GeneratedAt != initialGeneratedAt || !latest.RefreshInProgress))
                {
                    _refreshing = false;
                    break;
                }
            }
            catch
            {
                // Non-fatal transient HTTP error during polling
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task EnsureHubConnectedAsync()
    {
        if (_hub is not null && _hub.State == HubConnectionState.Connected)
            return;
        if (_hub is not null)
        { await _hub.DisposeAsync(); }

        _hub = new HubConnectionBuilder()
            .WithUrl(NavManager.ToAbsoluteUri("/hubs/refresh"))
            .WithAutomaticReconnect()
            .Build();

        // Bind the hub payload straight to a JsonElement — no reflection-based
        // re-serialise round-trip, which keeps this trim-safe.
        _hub.On<JsonElement>("RefreshProgress", root =>
        {
            var wasRefreshing = _refreshing;
            try
            {
                if (root.TryGetProperty("percent", out var pct))
                    _progressPercent = pct.GetInt32();
                if (root.TryGetProperty("step", out var step))
                    _progressStep = step.GetString() ?? "";
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    _refreshFailed = true;
                    _refreshFailureMessage = err.GetString();
                }
                bool done = root.TryGetProperty("done", out var d) && d.GetBoolean();
                if (done)
                    _refreshing = false;
            }
            catch { }

            // Walk the drone's filter to match the bar. Fire-and-forget: a progress tick is
            // the hottest path on this page, and awaiting an interop call here would
            // serialise the hub callback behind the audio graph.
            _ = SfxAsync("audioKit.refreshProgress", _progressPercent);

            // Route the tick to the progress strip ALONE.
            //
            // This used to be a bare InvokeAsync(StateHasChanged) on the page. A scan emits
            // roughly twenty of these over thirty seconds, and each one re-rendered the hero,
            // four sparklines, both metric panels, the 30-day uptime grid and — whenever
            // Advanced diagnostics was open — the whole priority queue and the virtualized
            // resource explorer, on the single WASM thread, to move one bar. None of that
            // markup depends on a percentage.
            //
            // The page still renders on the transition that ENDS the scan (`done`), because
            // that is when _refreshing flips and the strip has to come out of the tree; and it
            // still renders if the strip has not been assigned yet, which is the window
            // between _refreshing going true and its first render completing.
            var terminal = wasRefreshing && !_refreshing;
            InvokeAsync(() =>
            {
                if (terminal || _progressUi is null)
                    StateHasChanged();
                else
                    _progressUi.Update(_progressPercent, _progressStep,
                        _hub?.State == HubConnectionState.Connected);
            });
        });

        try
        {
            await _hub.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SignalR hub start connection error (refresh status updates will fall back to HTTP polling): {ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;

        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
