# Azure CLI escapes special characters in connection strings

> **Area:** Deployment
> **Date:** 2026-03-30

## Context
During the PostgreSQL migration, setting the connection string via `az webapp config connection-string set` with a password containing `!` resulted in the password being stored as `\!`. The app failed to start with a 500.30 error because Npgsql couldn't authenticate.

## Learning
`az webapp config connection-string set --settings Default="..."` passes the value through bash, which interprets `!` as history expansion even inside double quotes. Single quotes don't help because the Azure CLI still processes the value. The stored value ends up with `\!` instead of `!`.

**Fix:** Use `az rest` with a JSON file body to bypass shell interpolation entirely:

```bash
cat > /tmp/conn.json << 'EOF'
{
  "properties": {
    "Default": {
      "value": "Host=server.postgres.database.azure.com;Port=5432;Database=mydb;Username=admin;Password=Pass!word;SSL Mode=Require",
      "type": "Custom"
    }
  }
}
EOF

az rest --method PUT \
  --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Web/sites/$APP/config/connectionstrings?api-version=2023-01-01" \
  --body @/tmp/conn.json
```

This applies to both production and slot-specific connection strings (add `/slots/<name>` to the URL path).
