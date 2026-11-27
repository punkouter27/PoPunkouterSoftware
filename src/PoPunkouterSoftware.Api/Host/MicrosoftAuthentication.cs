using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PoPunkouterSoftware.Api.Platform;

namespace PoPunkouterSoftware.Api;

internal static class MicrosoftAuthentication
{
    internal const string SessionScheme = "PoPunkouterSoftware.Session";

    internal static void AddMicrosoftAuthentication(this WebApplicationBuilder builder)
    {
        var local = builder.Environment.IsDevelopment()
            || builder.Environment.IsEnvironment("Test") || builder.Environment.IsEnvironment("Testing");
        var auth = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = "PoPunkouterSoftware.Auth";
            options.DefaultChallengeScheme = SessionScheme;
        });
        auth.AddPolicyScheme("PoPunkouterSoftware.Auth", null, options =>
            options.ForwardDefaultSelector = context =>
                local && context.Request.Headers.ContainsKey(FakeAuthHandler.UserHeader)
                    ? FakeAuthHandler.SchemeName : SessionScheme);
        if (local)
            auth.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });

        auth.AddCookie(SessionScheme, options =>
        {
            options.Cookie.Name = PoPlatform.SessionCookieName(secure: !local);
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/auth/login/microsoft";
            // A session is only ever the owner's: re-checked on every request so removing an
            // account from MicrosoftAuth:OwnerEmail ends its session at once.
            options.Events.OnValidatePrincipal = async context =>
            {
                var owners = context.HttpContext.RequestServices.GetRequiredService<OwnerAccess>();
                if (!owners.IsOwner(context.Principal))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(SessionScheme);
                }
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return context.Response.WriteAsJsonAsync(new { status = 401, detail = "Microsoft sign-in is required." });
                }
                else
                    context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return context.Response.WriteAsJsonAsync(new { status = 403, detail = "Access is denied." });
            };
        });

        // Use an app-specific section: the shared vault also contains other apps' AzureAd settings.
        var config = builder.Configuration.GetSection("MicrosoftAuth");
        if (!string.IsNullOrWhiteSpace(config["ClientId"]) && !string.IsNullOrWhiteSpace(config["ClientSecret"]))
        {
            var tenant = config["TenantId"] ?? "common";
            auth.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
                options.ClientId = config["ClientId"];
                options.ClientSecret = config["ClientSecret"];
                options.SignInScheme = SessionScheme;
                options.ResponseType = "code";
                options.ResponseMode = "query";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                {
                    if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
                        && uri.Scheme == "https" && uri.Host == "login.microsoftonline.com"
                        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                        && uri.Segments.Length == 3 && uri.Segments[2] == "v2.0"
                        && Guid.TryParse(uri.Segments[1].Trim('/'), out var issuerTenant)
                        && (!Guid.TryParse(tenant, out var expectedTenant) || issuerTenant == expectedTenant))
                        return issuer;
                    throw new SecurityTokenInvalidIssuerException("The Microsoft token issuer is not valid for this app.");
                };
                options.Events.OnTokenValidated = context =>
                {
                    var owners = context.HttpContext.RequestServices.GetRequiredService<OwnerAccess>();
                    // An email claim is only proof of identity inside the owner's own directory.
                    if (owners.Owners.Count > 0 && !Guid.TryParse(tenant, out _))
                        context.Fail("MicrosoftAuth:TenantId must be the owner's tenant id when MicrosoftAuth:OwnerEmail is set.");
                    else if (!owners.IsOwner(context.Principal))
                        context.Fail("This Microsoft account is not the owner of this site.");
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("MicrosoftAuthentication").LogWarning(context.Failure, "Microsoft sign-in failed.");
                    context.HandleResponse();
                    context.Response.Redirect("/auth/error");
                    return Task.CompletedTask;
                };
            });
        }
        else
            Serilog.Log.Warning("Microsoft sign-in is unavailable: MicrosoftAuth:ClientId and ClientSecret are required.");

        builder.Services.AddSingleton<OwnerAccess>();
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
    }
}
