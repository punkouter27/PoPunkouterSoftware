using System.Security.Claims;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// The owner allow-list behind Microsoft sign-in. Everything past the public portfolio
/// (/azure, /users, /global and their APIs) is for the owner only, so an account that is not
/// on <c>MicrosoftAuth:OwnerEmail</c> never gets a session: it is refused when the Microsoft
/// token arrives, and an existing cookie is rejected on its next request.
/// </summary>
/// <remarks>
/// Fails closed. With no owner configured, nobody passes outside Development/Testing (where the
/// list is optional so a fresh clone and the test host still work). The email claims are only
/// trustworthy from the owner's own directory, which is why sign-in also requires
/// <c>MicrosoftAuth:TenantId</c> to be a tenant id when an owner is set.
/// </remarks>
internal sealed class OwnerAccess(IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly string[] EmailClaims = ["preferred_username", "email", ClaimTypes.Name, ClaimTypes.Email];

    private readonly bool _local = environment.IsDevelopment()
        || environment.IsEnvironment("Test") || environment.IsEnvironment("Testing");

    /// <summary>The configured owner emails (comma or semicolon separated in config).</summary>
    internal IReadOnlyList<string> Owners =>
        (configuration["MicrosoftAuth:OwnerEmail"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal bool IsOwner(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return false;

        var owners = Owners;
        if (owners.Count == 0)
            return _local;

        return principal.Claims.Any(claim => EmailClaims.Contains(claim.Type, StringComparer.Ordinal)
            && owners.Contains(claim.Value.Trim(), StringComparer.OrdinalIgnoreCase));
    }
}
