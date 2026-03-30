# TSV export truncates JSON columns during database migration

## Discovery
When migrating CurationCache from SQL Server to PostgreSQL using `sqlcmd` TSV output, the RowsJson column (1687 chars) was truncated to 254 chars. The tab-separated format split on a tab-like character inside the JSON, and `sqlcmd`'s `-y` flag has a max column width that can pad or truncate.

## Root cause
`sqlcmd` defaults to a narrow column width for output. Even with `-y 8000`, large text columns get padded to that width. The TSV format (`-s "\t"`) also can't safely handle JSON that contains escape sequences.

## Solution for production migration
For tables with large JSON/TEXT columns (like CurationCache), use one of:

1. **Skip the cache** — CurationCache regenerates on next page load (hash-based invalidation). Just migrate the source data tables and let caches rebuild.

2. **Use `bcp` with queryout** for reliable bulk export of large text columns:
   ```
   bcp "SELECT RowsJson FROM CurationCache" queryout file.txt -S server -d db -c -T
   ```

3. **Script the INSERT directly** — for small row counts, query the data via a .NET script that reads from SQL Server and writes to PostgreSQL, avoiding shell encoding issues entirely.

## Tables affected
Only `CurationCache.RowsJson` — all other tables have short, predictable column values that export cleanly via TSV.
