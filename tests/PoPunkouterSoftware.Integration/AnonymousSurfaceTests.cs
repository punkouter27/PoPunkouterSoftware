using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Pins every route a signed-out caller can reach in Production. There is no FallbackPolicy in
/// this app, so an endpoint is public unless it names a policy; a new public route fails this
/// test until it is added to the list on purpose. Static files and the Blazor host are public
/// by design and are identified by their metadata, not listed.
/// </summary>
public class AnonymousSurfaceTests(ProductionApp app) : IClassFixture<ProductionApp>
{
    private static readonly string[] AnonymousAllowList =
    [
        "/api/config",
        "/api/diag/",
        "/api/portfolio/",
        "/api/portfolio/badge/{id}.svg",
        "/api/portfolio/screenshots/{host}",
        "/api/visit",
        "/api/{**rest}",
        "/auth/error",
        "/auth/login/microsoft",
        "/favicon.ico",
        "/health",
        "/health/live",
        "/robots.txt",
        "/robots{suffix}.txt",
        "/{**path:file}",
    ];

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var anonymous = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        || e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
