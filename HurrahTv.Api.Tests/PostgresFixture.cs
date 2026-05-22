using System.Security.Cryptography;
using Dapper;
using HurrahTv.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HurrahTv.Api.Tests;

// shared across the whole assembly via [Collection("postgres")] so we pay
// the schema init exactly once. Each test class derives its own scope by
// calling ResetAsync() to truncate user-scoped tables.
//
// connection string discovery (first match wins):
//   1. env var HURRAHTV_TEST_CONNECTION — set in CI to point at the service container
//   2. fallback: Host=localhost;Database=hurrahtv_test;Username=postgres
//      (matches the appsettings.json convention; works against the brew-managed
//      Postgres on a dev machine with trust auth)
//
// the test database is auto-created on first run if missing, so `dotnet test`
// after a fresh clone needs nothing more than a running Postgres on localhost.
public class PostgresFixture : IAsyncLifetime
{
    private const string DefaultTestDbName = "hurrahtv_test";

    private static readonly string EnvConnection
        = Environment.GetEnvironmentVariable("HURRAHTV_TEST_CONNECTION") ?? string.Empty;

    public string ConnectionString { get; } = string.IsNullOrEmpty(EnvConnection)
        ? $"Host=localhost;Database={DefaultTestDbName};Username=postgres"
        : EnvConnection;

    // derived from ConnectionString rather than hardcoded — if HURRAHTV_TEST_CONNECTION
    // overrides the connection string to point at a different DB, the create/reset/run
    // paths all agree on the same target database.
    private string TestDbName => new NpgsqlConnectionStringBuilder(ConnectionString).Database
        ?? DefaultTestDbName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public const string JwtIssuer = "HurrahTvTest";

    public string JwtKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // env-var keys set in InitializeAsync; we unset each in DisposeAsync so a
    // multi-assembly `dotnet test` invocation doesn't leak the test JwtKey /
    // connection string / Twilio override into a sibling assembly that builds
    // its own WebApplicationFactory<Program> later in the same process.
    private static readonly string[] OverriddenEnvKeys =
    [
        "ASPNETCORE_ConnectionStrings__Default",
        "ASPNETCORE_Jwt__Key",
        "ASPNETCORE_Jwt__Issuer",
        "ASPNETCORE_Twilio__AccountSid",
        "ASPNETCORE_AI__Enabled",
    ];

    public async Task InitializeAsync()
    {
        await EnsureTestDatabaseExistsAsync();

        // Program.cs snapshots builder.Configuration["Jwt:Key"] into a local at the
        // top of WebApplication.CreateBuilder(args). InMemory providers registered via
        // ConfigureAppConfiguration land AFTER that snapshot, so they don't win.
        // Env vars (ASPNETCORE_ prefix + __ as separator) are read during the default
        // config load, BEFORE Program.cs captures the value — so they do win.
        //
        // Twilio:AccountSid is overridden to "" rather than null, because appsettings.json
        // ships with a non-null placeholder ("YOUR_TWILIO_ACCOUNT_SID") that would otherwise
        // win and trip SmsService into TwilioClient.Init with garbage creds.
        Environment.SetEnvironmentVariable("ASPNETCORE_ConnectionStrings__Default", ConnectionString);
        Environment.SetEnvironmentVariable("ASPNETCORE_Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("ASPNETCORE_Jwt__Issuer", JwtIssuer);
        Environment.SetEnvironmentVariable("ASPNETCORE_Twilio__AccountSid", "");
        Environment.SetEnvironmentVariable("ASPNETCORE_AI__Enabled", "false");

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                // intercept TmdbService's HttpClient so GET /api/queue's
                // fire-and-forget RefreshStaleItemsInBackground doesn't hit
                // api.themoviedb.org during integration tests.
                builder.ConfigureTestServices(services => services.AddHttpClient<TmdbService>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubTmdbHandler()));
            });

        // force the host to build so DbService.InitializeAsync runs the schema setup
        _ = Factory.Server;
    }

    // truncate user-scoped tables so each test starts from a clean slate.
    // RESTART IDENTITY resets the QueueItems serial so ids are deterministic.
    public async Task ResetAsync()
    {
        using NpgsqlConnection db = new(ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            TRUNCATE
                QueueItems, OtpCodes, UserServices, UserGenres, UserSettings,
                SeasonSentiments, EpisodeSentiments, WatchedEpisodes,
                AIUsage, CurationCache, Users
            RESTART IDENTITY CASCADE
            """);
    }

    // null-guard Factory in case InitializeAsync threw before Factory was assigned
    // (e.g. Postgres unreachable in EnsureTestDatabaseExistsAsync). Otherwise the
    // NRE here masks the real connection-refused error.
    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();

        foreach (string key in OverriddenEnvKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    // connects to the maintenance "postgres" DB to CREATE DATABASE if the target
    // doesn't exist yet. idempotent — safe to call on every test run.
    private async Task EnsureTestDatabaseExistsAsync()
    {
        string targetDb = TestDbName;
        NpgsqlConnectionStringBuilder b = new(ConnectionString) { Database = "postgres" };
        using NpgsqlConnection admin = new(b.ConnectionString);
        await admin.OpenAsync();

        bool exists = await admin.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @Name)",
            new { Name = targetDb });

        if (!exists)
        {
            // CREATE DATABASE can't be parameterized; identifier is sourced from the
            // connection string (controlled by HURRAHTV_TEST_CONNECTION or default).
            // Quote with double-quotes so non-ASCII / case-sensitive names work.
            await admin.ExecuteAsync($"CREATE DATABASE \"{targetDb.Replace("\"", "\"\"")}\"");
        }
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
