using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using HurrahTv.Api.Endpoints;
using HurrahTv.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<DbService>();
builder.Services.AddSingleton<SmsService>();
builder.Services.AddScoped<AuthService>();

// jwt auth
string jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("https://localhost:7267", "http://localhost:5271")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// initialize database
DbService db = app.Services.GetRequiredService<DbService>();
await db.InitializeAsync();

// map endpoints
app.MapAuthEndpoints();
app.MapSearchEndpoints();
app.MapDetailsEndpoints();
app.MapQueueEndpoints();
app.MapUserServiceEndpoints();

// health check
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).AllowAnonymous();

app.Run();
