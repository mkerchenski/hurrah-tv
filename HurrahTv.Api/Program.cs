using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using HurrahTv.Api.Endpoints;
using HurrahTv.Api.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<DbService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CurationService>();

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
builder.Services.AddAuthorization();

string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://localhost:7267", "http://localhost:5271"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()));

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

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

// serve Blazor WASM client from wwwroot (handles all MIME types, compression, _framework)
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        string path = ctx.Context.Request.Path.Value ?? "";

        if (ctx.Context.Request.Query.ContainsKey("v"))
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

// health check + version
string buildVersion = builder.Configuration["BuildVersion"] ?? "dev";
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).AllowAnonymous();
app.MapGet("/api/version", () => Results.Ok(new { version = buildVersion })).AllowAnonymous();

app.Run();
