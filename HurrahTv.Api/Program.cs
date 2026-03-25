using HurrahTv.Api.Endpoints;
using HurrahTv.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSingleton<DbService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("https://localhost:7267", "http://localhost:5271")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();

// initialize database
DbService db = app.Services.GetRequiredService<DbService>();
await db.InitializeAsync();

// map endpoints
app.MapSearchEndpoints();
app.MapDetailsEndpoints();
app.MapQueueEndpoints();
app.MapUserServiceEndpoints();

// health check
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();
