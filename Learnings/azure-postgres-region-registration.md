# Azure PostgreSQL Flexible Server: region and provider registration

> **Area:** Deployment
> **Date:** 2026-03-30

## Context
Tried to create a PostgreSQL Flexible Server in `eastus` (same region as the App Service) and hit two blockers back-to-back.

## Learning
1. **Region restriction:** PostgreSQL Flexible Server provisioning is not available in all regions. `eastus` was restricted; `eastus2` worked. Check availability before planning.

2. **Provider registration:** First-time use of a resource type requires registering the namespace on the subscription. Without it, you get `MissingSubscriptionRegistration`:
   ```bash
   az provider register --namespace Microsoft.DBforPostgreSQL --wait
   ```
   This is a one-time step per subscription, takes ~30 seconds.

3. **Cross-region is fine:** The App Service in `eastus` connects to PostgreSQL in `eastus2` with no noticeable latency for a low-traffic app. Azure regions in the same geography have fast interconnects.

## Example
```bash
# check if a region supports the SKU you want
az postgres flexible-server list-skus --location eastus2 --query "length(@)" -o tsv
```
