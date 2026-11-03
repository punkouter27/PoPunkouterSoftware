namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One catalog app's outward links, checked by the scan without credentials, so the home page
/// only links what a visitor can actually open: a private repo answers 404 to an anonymous
/// browser, and an unpublished (draft or closed-testing) Play listing does too.
/// Null means "not checked or the check failed" — never read as private or unpublished.
/// </summary>
public record PublicLinkCheck
{
    public string App { get; init; } = "";
    /// <summary>"owner/repo" from apps.json.</summary>
    public string? Repo { get; init; }
    public bool? RepoPublic { get; init; }
    /// <summary>Android application id from apps.json, e.g. "com.punkoutersoftware.pobox".</summary>
    public string? AndroidPackage { get; init; }
    public bool? PlayStoreLive { get; init; }
}
