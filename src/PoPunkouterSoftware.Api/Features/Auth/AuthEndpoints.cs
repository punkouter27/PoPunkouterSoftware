using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace PoPunkouterSoftware.Api;

internal static class AuthEndpoints
{
    internal static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/error", () => Results.Problem(
            detail: "Microsoft sign-in failed. Only the site owner's account can sign in; the portfolio stays open to everyone.", statusCode: 401))
            .AllowAnonymous().WithTags("Auth");

        app.MapGet("/auth/login/microsoft", (string? returnUrl, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(config["MicrosoftAuth:ClientId"])
                || string.IsNullOrWhiteSpace(config["MicrosoftAuth:ClientSecret"]))
                return Results.Problem(detail: "Microsoft sign-in is not configured on this deployment.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = SafeLocalUrl(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).AllowAnonymous().WithTags("Auth");

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(detail: "The sign-out request could not be verified.", statusCode: 400);
            }
            await context.SignOutAsync(MicrosoftAuthentication.SessionScheme);
            return Results.Redirect("/");
        }).RequireAuthorization().WithTags("Auth");
    }

    internal static string SafeLocalUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\')
        && !url.Any(char.IsControl) ? url : "/";
}
