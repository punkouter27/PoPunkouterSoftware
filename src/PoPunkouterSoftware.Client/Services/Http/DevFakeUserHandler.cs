namespace PoPunkouterSoftware.Client;

/// <summary>
/// Injects <c>X-Fake-User</c> and <c>X-Fake-Roles</c> headers on same-origin API requests when
/// a non-anonymous developer persona is active in <see cref="DevPersonaState"/>.
/// </summary>
internal sealed class DevFakeUserHandler : DelegatingHandler
{
    private readonly DevPersonaState _personaState;

    public DevFakeUserHandler(DevPersonaState personaState) => _personaState = personaState;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_personaState.FakeUser is { } user)
        {
            request.Headers.Remove("X-Fake-User");
            request.Headers.Add("X-Fake-User", user);
        }

        if (_personaState.FakeRoles is { } roles)
        {
            request.Headers.Remove("X-Fake-Roles");
            request.Headers.Add("X-Fake-Roles", roles);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
