using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HurrahTv.Client;
using HurrahTv.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// auth services
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<HurrahAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<HurrahAuthStateProvider>());
builder.Services.AddAuthorizationCore();

// http client with auth handler
string apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7201";
builder.Services.AddScoped<AuthMessageHandler>();
builder.Services.AddScoped(sp =>
{
    AuthMessageHandler handler = sp.GetRequiredService<AuthMessageHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBaseUrl) };
});
builder.Services.AddScoped<ApiClient>();

await builder.Build().RunAsync();
