using FluentValidation;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared;
using Radzen;

// SOLID: Single Responsibility — this file only wires WASM DI and starts the host.
// GoF:   Builder — WebAssemblyHostBuilder assembles the WASM app before Run().

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// ─── HttpClient + Dev persona & retry pipeline ──────────────────────────────
builder.Services.AddSingleton<DevPersonaState>();
builder.Services.AddScoped(sp =>
{
    var persona = sp.GetRequiredService<DevPersonaState>();
    var retryHandler = new TransientRetryHandler { InnerHandler = new HttpClientHandler() };
    var fakeUserHandler = new DevFakeUserHandler(persona) { InnerHandler = retryHandler };
    return new HttpClient(fakeUserHandler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
        Timeout = TimeSpan.FromMinutes(5),
    };
});
builder.Services.AddScoped<PortfolioApiClient>();
builder.Services.AddValidatorsFromAssemblyContaining<SignInSearchFilterValidator>();

// ─── Radzen UI services ───────────────────────────────────────────────────────
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<DialogNotificationHelper>();

await builder.Build().RunAsync();
