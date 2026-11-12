using Microsoft.AspNetCore.SignalR;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Simple SignalR hub for pushing live refresh progress updates to connected clients.
/// Server-side components (DiagEndpoints) inject IHubContext&lt;RefreshHub&gt; and call
///   Clients.All.SendAsync("RefreshProgress", payload)
/// from inside the background refresh task.
/// </summary>
public sealed class RefreshHub : Hub
{
    // No server-side methods required.
    // All pushes originate from IHubContext<RefreshHub> injected into DiagEndpoints.
}

