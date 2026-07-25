# HurrahTv.Api.Tests — local setup

The fixture connects to local Postgres on `localhost:5432` as user `postgres` and auto-creates the `hurrahtv_test` database on first run. Override with the `HURRAHTV_TEST_CONNECTION` env var if your local creds differ. CI uses a `postgres:16-alpine` service container with trust auth so the default works there too.

Testing policy (what needs a test, TDD bias, style) lives in the root `CLAUDE.md`.
