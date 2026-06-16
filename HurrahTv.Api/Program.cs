using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using HurrahTv.Api.Authorization;
using HurrahTv.Api.Endpoints;
using HurrahTv.Api.Middleware;
using HurrahTv.Api.Services;
using HurrahTv.Api.Telemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<DbService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CurationService>();
// drain in-flight detached AIUsage writes on host shutdown (#123). Registered as
// both a hosted service (StopAsync drain) and a singleton so CurationService can
// resolve it for Register(Task). Same instance for both — hosted services are
// singletons by default; the explicit AddSingleton(sp => GetRequiredService) keeps
// the lookup path unified.
builder.Services.AddSingleton<AIUsageDrainHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIUsageDrainHostedService>());

// short build SHA stamped by CI (drives /api/version and the App Insights component version)
string buildVersion = builder.Configuration["BuildVersion"] ?? "dev";

// Application Insights (#24/#200) — only when a real connection string is present, so
// dev/local and the committed "YOUR_…" placeholder send nothing. Azure App Service supplies
// APPLICATIONINSIGHTS_CONNECTION_STRING; we read it (or the config section) explicitly rather
// than relying on a ?? fallback, which the base appsettings.json would render dead code
// (Learnings/aspnet-config-get-null-coalesce-trap.md).
string? appInsightsConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(appInsightsConnection) && !appInsightsConnection.StartsWith("YOUR_"))
{
    builder.Services.AddApplicationInsightsTelemetry(options => options.ConnectionString = appInsightsConnection);
    // SDK 3.x is OpenTelemetry-based: scrub PII with a span processor and stamp the deploy SHA
    // as the service version (both replace the removed 2.x ITelemetryInitializer)
    builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
    {
        tracing.AddProcessor(new PiiRedactionProcessor());
        tracing.ConfigureResource(resource => resource.AddService("HurrahTv.Api", serviceVersion: buildVersion));
    });
}

// jwt auth
string jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "HurrahTv",
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtKey))
        };
    });
builder.Services.AddScoped<IAuthorizationHandler, AdminRequirementHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.AddRequirements(new AdminRequirement()));

string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (builder.Environment.IsDevelopment())
    corsOrigins = [.. corsOrigins, "https://localhost:7267", "http://localhost:5271"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              // expose Server-Timing so the RUM beacon (#201) can read it cross-origin in dev
              // (client :7267 → api :7201); in prod the same-origin instance exposes it anyway
              .WithExposedHeaders("Server-Timing")));

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

// outermost: time the whole request and emit Server-Timing for the RUM beacon (#201/#200)
app.UseMiddleware<ResponseTimingMiddleware>();

// redirect www.{hurrah.tv,staging.hurrah.tv} → apex. Destination hosts are hardcoded constants
// (not derived from the request) to eliminate open-redirect via spoofed Host header.
app.Use(async (context, next) =>
{
    string? apexHost = context.Request.Host.Host.ToLowerInvariant() switch
    {
        "www.hurrah.tv" => "hurrah.tv",
        "www.staging.hurrah.tv" => "staging.hurrah.tv",
        _ => null
    };
    if (apexHost is not null)
    {
        string newUrl = $"{context.Request.Scheme}://{apexHost}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect(newUrl, permanent: true);
        return;
    }
    await next();
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Order matters here: this middleware MUST stay after UseAuthentication/UseAuthorization
// and before UseBlazorFrameworkFiles / MapFallbackToFile.
//   - After auth: so HttpContext.User is populated if we ever need it, and so expired-token
//     real-user traffic still goes through the auth pipeline normally (bots have no JWT and
//     short-circuit before any [Authorize] endpoint is hit, but a future endpoint move could
//     trip on this if the middleware were promoted above auth).
//   - Before fallback: so /details/{tv|movie}/{id} bot requests are intercepted before
//     MapFallbackToFile serves index.html.
// See issue #98. Do not move above UseAuthorization without re-auditing the auth implications.
app.UseMiddleware<OgPreviewMiddleware>();

// serve Blazor WASM client from wwwroot (handles all MIME types, compression, _framework)
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        string path = ctx.Context.Request.Path.Value ?? "";

        // service-worker.js must always revalidate so a new deploy's SW replaces the old
        // one — checked before the ?v= branch so a future fingerprinted SW URL can't pin
        // it immutable for a year (issue #15)
        if (path.EndsWith("service-worker.js"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        else if (ctx.Context.Request.Query.ContainsKey("v"))
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        else if (path.EndsWith("index.html"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        else
            ctx.Context.Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
    }
});
// MapFallbackToFile bypasses StaticFileOptions, so set no-cache via its own options
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

// initialize database
DbService db = app.Services.GetRequiredService<DbService>();
await db.InitializeAsync();

// map endpoints
app.MapAuthEndpoints();
app.MapSearchEndpoints();
app.MapDetailsEndpoints();
app.MapQueueEndpoints();
app.MapEpisodeEndpoints();
app.MapSentimentEndpoints();
app.MapUserServiceEndpoints();
app.MapCurationEndpoints();
app.MapAdminEndpoints();
app.MapProfileEndpoints();

// health check + version (buildVersion computed above, near the App Insights registration)
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).AllowAnonymous();
app.MapGet("/api/version", () => Results.Ok(new { version = buildVersion })).AllowAnonymous();

app.Run();
