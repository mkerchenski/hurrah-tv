# PostgreSQL passwordless localhost via pg_hba.conf trust

> **Area:** Deployment
> **Date:** 2026-03-30

## Context
After installing PostgreSQL on Windows via `winget install PostgreSQL.PostgreSQL.17`, the default auth method is `scram-sha-256` for all connections including localhost. This requires a password even for local dev, which adds friction.

## Learning
Edit `C:\Program Files\PostgreSQL\17\data\pg_hba.conf` and change the method for local connections from `scram-sha-256` to `trust`:

```
# IPv4 local connections:
host    all    all    127.0.0.1/32    trust
# IPv6 local connections:
host    all    all    ::1/128         trust
```

Reload without restarting the service (avoids needing admin):
```sql
SELECT pg_reload_conf();
```

On Mac, `brew install postgresql` defaults to `trust` for localhost — so this brings Windows in line.

Connection string without password: `Host=localhost;Database=HurrahTv;Username=postgres`
