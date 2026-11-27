namespace PoPunkouterSoftware.Api;

/// <summary>Management actions require a session, the feature flag, and a same-origin request header.</summary>
internal sealed class ManagementActionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        var enabled = config.GetValue("FeatureFlags:EnableManagementActions",
            env.IsDevelopment() || env.IsEnvironment("Test") || env.IsEnvironment("Testing"));
        if (!enabled)
            return Results.Problem(detail: "Management actions are disabled in this environment.", statusCode: 403);

        // Cross-site forms cannot send this header; the host does not enable CORS.
        var origin = http.Request.Headers.Origin.ToString();
        if (http.Request.Headers["X-Management-Request"] != "1"
            || (!string.IsNullOrEmpty(origin) && origin != $"{http.Request.Scheme}://{http.Request.Host}"))
            return Results.Problem(detail: "A same-origin management request is required.", statusCode: 403);

        return await next(context);
    }
}

internal static class ManagementActionFilterExtensions
{
    public static RouteHandlerBuilder RequireManagementActions(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization().AddEndpointFilter<ManagementActionFilter>();
}
