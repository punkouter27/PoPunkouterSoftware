namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Cross-cutting fuzzy-name-matching helper, shared by the services that correlate one
/// Azure/GitHub name against another (App Service ↔ GitHub repo, App Service ↔ App Insights
/// component) so the matching rule is defined once instead of drifting between call sites.
/// </summary>
public static class NameMatching
{
    /// <summary>True when either string contains the other, case-insensitively.</summary>
    public static bool FuzzyContains(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The single identity a web service is recorded and graphed under — its friendly name,
    /// falling back to the Azure resource name.
    ///
    /// <para>Defined once because the uptime grid joins two independently written sources on
    /// this string. <c>HistorySummaryMapper</c> wrote the friendly name ("PoMemeVideo") while
    /// <c>ServicePingerService</c> wrote the resource name ("app-pomemevideo2"), so every
    /// service appeared as two half-populated rows — 16 rows for 8 apps, each showing a
    /// fraction of the evidence. Any new writer into that partition must use this.</para>
    /// </summary>
    public static string ServiceIdentity(string? friendlyName, string? resourceName) =>
        string.IsNullOrWhiteSpace(friendlyName) ? resourceName ?? "" : friendlyName;

    /// <summary>Letters and digits only, lower-cased — "app-pomemevideo2" and "PoMemeVideo"
    /// differ only by the "app" prefix once normalized.</summary>
    public static string Normalize(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
