using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HurrahTv.Client;
using HurrahTv.Client.Services;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// auth services
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<HurrahAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<HurrahAuthStateProvider>());
builder.Services.AddAuthorizationCore(options =>
{
    // client-side gate is a UI hint based on the JWT claim; the API re-checks against the DB
    options.AddPolicy("Admin", policy => policy.RequireClaim("is_admin", "true"));
});

// http client with auth handler
// in production, WASM is served from the API (same origin), so use the host URL
string apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped<AuthMessageHandler>();
builder.Services.AddScoped(sp =>
{
    AuthMessageHandler handler = sp.GetRequiredService<AuthMessageHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBaseUrl) };
});
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<UserServicesCache>();
builder.Services.AddSingleton<QuickActionService>();
builder.Services.AddScoped<MediaFilterService>();

await builder.Build().RunAsync();
