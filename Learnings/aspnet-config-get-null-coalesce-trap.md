# ASP.NET Core Config: `Get<T>() ?? fallback` Doesn't Do What You Think

> **Area:** API | Deployment
> **Date:** 2026-04-23

## Context

A commit moved CORS allowed origins to config "with localhost fallback":

```csharp
string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://localhost:7267", "http://localhost:5271"];
```

`appsettings.json` (base, loaded in every environment) had `Cors:AllowedOrigins = ["https://hurrah.tv"]`. `appsettings.Development.json` didn't override it. In dev, the client at `https://localhost:7267` got blocked by CORS — the "fallback" was dead code.

## Learning

`IConfiguration.GetSection(...).Get<T>()` returns null only when **no loaded config source** has the key at all. Layered config (`appsettings.json` → `appsettings.{Env}.json` → env vars → user secrets) means the base file almost always supplies *some* value, so `?? fallback` for dev-specific defaults is a trap.

Two patterns that actually work:

1. **Explicit environment branch** — don't rely on null-coalescing for env-specific behavior:
   ```csharp
   string[] corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
   if (env.IsDevelopment())
       corsOrigins = [.. corsOrigins, "https://localhost:7267", "http://localhost:5271"];
   ```

2. **Override the key in `appsettings.Development.json`** — fine if every contributor is willing to edit their gitignored dev config. Not great for onboarding.

Option 1 is preferred for anything a fresh contributor should get for free.

## Related trap

The same pattern burns you with single-value config too:
```csharp
// returns base value, NOT "dev-default", if appsettings.json sets the key
string model = config["AI:CurationModel"] ?? "dev-default";
```

If you want an environment-conditional default, branch on `env.IsDevelopment()` — don't rely on `??`.

## Detection

If you're writing `?? ["localhost", ...]` or `?? "dev-default"` in `Program.cs`, ask: "does the base `appsettings.json` have this key?" If yes, the fallback is unreachable.
