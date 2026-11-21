using FluentValidation;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
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
// Registered one by one, never by assembly scan: the published WASM bundle is trimmed, and a scan
// reaches each validator only through reflection, so the trimmer dropped SignInSearchFilterValidator's
// constructor and /users rendered nothing in production ("NoConstructorMatch"). A generic registration
// tells the trimmer to keep the constructor.
builder.Services.AddSingleton<IValidator<SignInSearchFilter>, SignInSearchFilterValidator>();
builder.Services.AddSingleton<IValidator<AiTriageAdHocRequest>, AiTriageAdHocRequestValidator>();

// ─── Radzen UI services ───────────────────────────────────────────────────────
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<DialogNotificationHelper>();

var host = builder.Build();

// A developer persona saved in this browser (localStorage, DevPersonaState.StorageKey) is
// restored here. Until 2026-11-18 the header's AccountMenu did it and also showed a dropdown
// to change it; the header is now the estate's (brand + settings gear) and the dropdown went.
try
{
    var js = host.Services.GetRequiredService<IJSRuntime>();
    var saved = await js.InvokeAsync<string?>("localStorage.getItem", DevPersonaState.StorageKey);
    if (!string.IsNullOrWhiteSpace(saved))
        host.Services.GetRequiredService<DevPersonaState>().SetPersona(saved);
}
catch (JSException)
{
    // localStorage unavailable in strict privacy contexts: the guest persona stands.
}

await host.RunAsync();
