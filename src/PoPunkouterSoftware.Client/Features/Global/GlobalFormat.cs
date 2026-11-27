using System.Globalization;

namespace PoPunkouterSoftware.Client;

/// <summary>Formatting shared by the <c>/global</c> pages.</summary>
internal static class GlobalFormat
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null or 0)
            return "0 GB";

        double value = Math.Abs(bytes.Value);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = unit <= 1
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }

    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Ago(DateTimeOffset? when) =>
        when is { } value ? RelativeTime.FormatDetailed(DateTimeOffset.UtcNow - value) : "never";

    public static string Milliseconds(double? ms) =>
        ms is null or 0 ? "—" : ms.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>Status tone for a GitHub Actions conclusion.</summary>
    public static string CiTone(string? conclusion) => conclusion?.ToLowerInvariant() switch
    {
        "success" => "good",
        "failure" or "timed_out" or "action_required" => "bad",
        "cancelled" => "warn",
        _ => "muted",
    };

    public static string StageTone(string stage) => stage switch
    {
        "shipped" or "ready" => "good",
        "polishing" => "warn",
        "playable" => "info",
        _ => "muted",
    };

    /// <summary>Workspace-relative path → the GitHub URL for the same file, when the remote is known.</summary>
    public static string? GitHubPath(string? remote, string? slug, string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return null;

        var repo = slug is { Length: > 0 } s
            ? s
            : remote?.TrimEnd('/')
                .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        return repo is { Length: > 0 } ? $"https://github.com/{repo}/blob/HEAD/{relativePath}" : null;
    }

    /// <summary>The repo a workspace-relative path belongs to (<c>PoBox/Assets/x.onnx</c> → <c>PoBox</c>).</summary>
    public static string RepoOf(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}
