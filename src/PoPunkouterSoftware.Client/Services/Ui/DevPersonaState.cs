namespace PoPunkouterSoftware.Client;

/// <summary>
/// Client-side state for the optional local developer persona switcher (<c>X-Fake-User</c> /
/// <c>X-Fake-Roles</c>) used in Development and Test environments.
/// </summary>
public sealed class DevPersonaState
{
    public const string StorageKey = "popunkoutersoftware:dev-user";

    public string Persona { get; private set; } = "guest";

    public bool IsManagement => string.Equals(Persona, "admin", StringComparison.OrdinalIgnoreCase);

    public string? FakeUser => IsManagement ? "dev-admin" : null;

    public string? FakeRoles => IsManagement ? "management" : null;

    public event Action? Changed;

    public void SetPersona(string persona)
    {
        var normalized = string.Equals(persona, "admin", StringComparison.OrdinalIgnoreCase)
            ? "admin"
            : "guest";

        if (string.Equals(Persona, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Persona = normalized;
        Changed?.Invoke();
    }
}
