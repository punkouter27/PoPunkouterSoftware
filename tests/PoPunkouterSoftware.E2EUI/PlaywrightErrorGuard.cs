using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// Attaches Playwright console error, uncaught pageerror, crash, requestfailed, and unhandled
/// HTTP >= 400 response watchers to a page and fails on <see cref="DisposeAsync"/> if triggered.
/// </summary>
internal sealed class PlaywrightErrorGuard : IAsyncDisposable
{
    private readonly IPage _page;
    private readonly string[] _allowedErrorUrlSubstrings;
    private readonly List<string> _failures = [];

    private PlaywrightErrorGuard(IPage page, string[] allowedErrorUrlSubstrings)
    {
        _page = page;
        _allowedErrorUrlSubstrings = allowedErrorUrlSubstrings;
        _page.Console += OnConsole;
        _page.PageError += OnPageError;
        _page.Crash += OnCrash;
        _page.RequestFailed += OnRequestFailed;
        _page.Response += OnResponse;
    }

    public static PlaywrightErrorGuard Watch(IPage page, params string[] allowedErrorUrlSubstrings) =>
        new(page, allowedErrorUrlSubstrings);

    private void OnConsole(object? sender, IConsoleMessage msg)
    {
        if (msg.Type == "error")
        {
            _failures.Add($"browser console.error: {msg.Text}");
        }
    }

    private void OnPageError(object? sender, string err) =>
        _failures.Add($"browser pageerror: {err}");

    private void OnCrash(object? sender, IPage page) =>
        _failures.Add("browser page crashed");

    private void OnRequestFailed(object? sender, IRequest request)
    {
        if (_allowedErrorUrlSubstrings.Any(s => request.Url.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (string.Equals(request.Failure, "net::ERR_ABORTED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _failures.Add($"request failed: {request.Method} {request.Url} ({request.Failure})");
    }

    private void OnResponse(object? sender, IResponse response)
    {
        if (response.Status < 400)
        {
            return;
        }

        if (_allowedErrorUrlSubstrings.Any(s => response.Url.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _failures.Add($"HTTP {response.Status}: {response.Request.Method} {response.Url}");
    }

    public async ValueTask DisposeAsync()
    {
        _page.Console -= OnConsole;
        _page.PageError -= OnPageError;
        _page.Crash -= OnCrash;
        _page.RequestFailed -= OnRequestFailed;
        _page.Response -= OnResponse;

        try
        {
            var errorUiVisible = await _page.EvaluateAsync<bool>(
                "() => { const el = document.getElementById('blazor-error-ui'); return !!el && getComputedStyle(el).display !== 'none'; }");
            if (errorUiVisible)
            {
                _failures.Add("#blazor-error-ui became visible");
            }
        }
        catch
        {
            // Page or browser already closed.
        }

        _failures.Should().BeEmpty("browser UI flows must complete with no console errors, failed assets, or unhandled HTTP 4xx/5xx responses");
    }
}
