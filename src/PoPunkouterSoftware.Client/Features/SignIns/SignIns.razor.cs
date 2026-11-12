using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// State and loading for the <c>/users</c> roster. The markup is in SignIns.razor and the
/// three blocks it composes are sibling components, following the same split AzureDashboard
/// uses.
/// </summary>
public partial class SignIns
{
    /// <summary>
    /// 90 is not a taste call: it is the retention on the shared Application Insights
    /// component, and <c>SignInQueryService.MaxWindowDays</c> clamps to it server-side. A
    /// fourth, longer choice would offer history that does not exist.
    /// </summary>
    private static readonly int[] WindowChoices = [7, 30, 90];

    private SignInReport? _report;
    private bool _loading = true;
    private string? _loadError;
    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "days")]
    public int? QueryDays { get; set; }

    private int _windowDays = 30;
    private string _personSearch = string.Empty;
    private string? _searchValidationError;

    private void OnSearchInput(ChangeEventArgs e)
    {
        var next = e.Value?.ToString() ?? string.Empty;
        var result = SearchValidator.Validate(new SignInSearchFilter { Query = next });
        _searchValidationError = result.IsValid ? null : result.Errors[0].ErrorMessage;
        _personSearch = next;
    }

    private void ClearSearch()
    {
        _personSearch = string.Empty;
        _searchValidationError = null;
    }

    private IReadOnlyList<SignInPerson> FilteredPeople =>
        _report?.People is null ? []
        : string.IsNullOrWhiteSpace(_personSearch) ? _report.People
        : _report.People.Where(p =>
            (!string.IsNullOrEmpty(p.DisplayName) && p.DisplayName.Contains(_personSearch, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(p.MaskedEmail) && p.MaskedEmail.Contains(_personSearch, StringComparison.OrdinalIgnoreCase)) ||
            p.Apps.Any(a => a.Contains(_personSearch, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>The $/person column only when some app's own group billed something — on the
    /// live estate every value was $0.00, a column of zeros.</summary>
    private bool ShowCost => _report?.Apps.Any(a => a.CostPerPerson is > 0) == true;

    /// <summary>Bar width under each app name: its reach as a share of the widest app's.</summary>
    private int ReachPercent(SignInAppReach app)
    {
        var max = _report?.Apps.Max(a => (int?)a.People) ?? 0;
        return max == 0 ? 0 : (int)Math.Round(app.People * 100d / max);
    }

    /// <summary>
    /// The window a visitor last picked, remembered in this browser only. This replaced the
    /// "Default sign-in window" setting on the deleted /settings page: the same preference,
    /// learned from the picker instead of configured on a page of its own.
    /// </summary>
    private const string WindowKey = "popunkoutersoftware:signin-days";

    /// <summary>Comets play once per visit, on the first load — a window switch re-reads the
    /// same recent arrivals and must not replay them.</summary>
    private bool _cometsPlayed;

    protected override async Task OnInitializedAsync()
    {
        if (QueryDays.HasValue && WindowChoices.Contains(QueryDays.Value))
        {
            _windowDays = QueryDays.Value;
        }
        else
        {
            try
            {
                if (int.TryParse(await JS.InvokeAsync<string?>("localStorage.getItem", WindowKey), out var saved)
                    && WindowChoices.Contains(saved))
                    _windowDays = saved;
            }
            catch { } // prerender, blocked storage: the default window is fine
        }

        await LoadAsync();
    }

    private async Task SetWindowAsync(int days)
    {
        if (days == _windowDays || _loading)
            return;

        _windowDays = days;
        try
        {
            NavManager.NavigateTo($"/users?days={days}", replace: true);
            await JS.InvokeVoidAsync("localStorage.setItem", WindowKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading && _report is not null)
            return;

        _loading = true;
        _loadError = null;
        StateHasChanged();

        try
        {
            // Per-call budget. The shared HttpClient's timeout is sized for the huge report
            // endpoint; the server already caps this query at 20s and degrades rather than
            // hanging, so anything past 30s here is the network, not the query.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var loaded = await Api.GetSignInsAsync(_windowDays, cts.Token);

            // Replace, never mutate: every ShouldRender guard on this page is reference
            // identity on these lists, and an in-place edit would be invisible to all of them.
            if (loaded is not null)
            {
                _report = loaded;

                if (!_cometsPlayed)
                {
                    _cometsPlayed = true;
                    await PlayArrivalsAsync(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed reload leaves _report populated on purpose — see the banner comment in
            // the markup. Only a first load with nothing to fall back on renders empty.
            _loadError = ex.Message;
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// One comet across the GPU backdrop (and one soft pluck, if sound is on) per sign-in in
    /// the last 24 hours, capped at six by js/gpu-backdrop.js. 24h rather than 1h: at this
    /// estate's traffic an hour is nearly always empty, and "who showed up today" is the
    /// question the page opens on. Decorative, so every interop failure is swallowed.
    /// </summary>
    private async Task PlayArrivalsAsync(SignInReport loaded)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var arrivals = loaded.Recent.Count(e => e.Timestamp >= since);
        if (arrivals == 0)
            return;

        try
        {
            await JS.InvokeVoidAsync("appFx.comets", arrivals);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }
}
