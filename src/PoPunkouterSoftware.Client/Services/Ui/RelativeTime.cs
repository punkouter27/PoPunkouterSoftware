namespace PoPunkouterSoftware.Client;

/// <summary>
/// Shared "time ago" formatting (deduplicated from Index.razor and AzureDashboard).
/// </summary>
public static class RelativeTime
{
    /// <summary>Short form: "Xm ago" / "Xh ago" / "Xd ago"; "Unknown age" when the timestamp is missing.</summary>
    public static string Format(DateTime? generatedAtUtc) =>
        generatedAtUtc is DateTime generated ? Format(DateTime.UtcNow - generated) : "Unknown age";

    /// <summary>Short form: "Xm ago" / "Xh ago" / "Xd ago"; "Unknown age" for <see cref="TimeSpan.MaxValue"/>.</summary>
    public static string Format(TimeSpan age) =>
        age == TimeSpan.MaxValue ? "Unknown age" :
        age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" :
        age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";

    /// <summary>Detailed form used by the Azure dashboard: "just now" / "Xm ago" / "Xh Ym ago" / "Xd Yh ago".</summary>
    public static string FormatDetailed(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
            return "just now";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24)
            return $"{(int)age.TotalHours}h {age.Minutes}m ago";
        return $"{(int)age.TotalDays}d {age.Hours}h ago";
    }
}
