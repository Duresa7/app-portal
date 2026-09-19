# M1-01: SQLite storage layer

**Milestone:** 1 (0.3.0)
**Depends on:** none
**Unlocks:** M1-02, M1-03

## Goal

Replace the three JSON files (catalog, devices, installs) with one SQLite database that every later package extends. Existing deployments migrate on first start without losing devices or install history.

## Context

- Stores today: `src/AppPortal.Server/Catalog/CatalogStore.cs` (read-only, loaded from `Portal:CatalogPath`), `Devices/DeviceStore.cs` (JSON with temp-file rename and reload on stamp change), `Installs/InstallStore.cs` (JSON, `Upsert` drops stale writes and never regresses a terminal state).
- Behaviours the README promises and tests cover, which must survive: one install per device at a time wins (`InstallService.CreateAsync` per-device gate), a finished install stays finished, a damaged file does not take the API down. See `tests/AppPortal.Server.Tests/ConcurrencyAndDurabilityTests.cs`.
- The Dockerfile sets `Portal__DataDirectory=/app/data` on a named volume; the config volume is read-only.

## Scope

### In
- `Microsoft.Data.Sqlite` (no EF Core). A `Database` service that opens `Portal:DataDirectory/app-portal.db`, enables WAL and foreign keys, and applies numbered migrations from embedded SQL files, recording them in `schema_version`.
- Migration 001 creates the tables in *Interface*.
- Rewrite `CatalogStore`, `DeviceStore`, `InstallStore` on top of the database with unchanged public methods so `InstallService`, the poller, the CLI and the endpoints compile without change.
- First-start import: if `catalog_apps` is empty and `Portal:CatalogPath` exists, import it. If `devices` is empty and `Portal:DevicesPath` exists, import it. If `installs` is empty and `DataDirectory/installs.json` exists, import it. Log each import. Never import twice.
- CLI: `catalog import <file>` (upsert by id), `catalog export` (prints JSON in the checked-in format). `catalog verify` keeps working. `device add`, `device list`, `device remove` keep working.
- Tests use a temp-file database; `WebApplicationFactory` tests point `Portal:DataDirectory` at a temp folder.

### Out
- Any new columns beyond those listed. Admin pages. Requests. Enrollment.

## Interface

Tables created by migration 001. Later plans add columns and tables by new migrations only.

```sql
schema_version(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);

catalog_apps(
  id TEXT PRIMARY KEY, name TEXT NOT NULL, publisher TEXT NOT NULL DEFAULT '',
  description TEXT NOT NULL DEFAULT '', category TEXT NOT NULL DEFAULT '',
  icon_url TEXT, featured INTEGER NOT NULL DEFAULT 0,
  match_json TEXT,                 -- the "match" object from catalog.json, or NULL
  engine_override TEXT,            -- 'action1' | 'agent' | NULL (use server preference); used from M3-05
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL);

catalog_packages(
  app_id TEXT NOT NULL REFERENCES catalog_apps(id) ON DELETE CASCADE,
  engine TEXT NOT NULL,            -- 'action1' | 'agent'
  definition_json TEXT NOT NULL,   -- action1: {"packageId":"...","version":"latest"}
  PRIMARY KEY(app_id, engine));

devices(
  id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, token_hash TEXT NOT NULL UNIQUE,
  enabled INTEGER NOT NULL DEFAULT 1,
  action1_endpoint_id TEXT,        -- NULL when the device has no Action1 endpoint
  has_agent INTEGER NOT NULL DEFAULT 0,
  engine_preference TEXT,          -- per-device override, NULL normally
  enrolled_with_key_id TEXT,       -- set by M2-01
  agent_version TEXT, last_seen_at TEXT,
  created_at TEXT NOT NULL);

installs(
  id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES devices(id),
  app_id TEXT NOT NULL, app_name TEXT NOT NULL,
  requested_by TEXT,               -- filled by M1-02
  engine TEXT NOT NULL DEFAULT 'action1',
  external_ref TEXT,               -- Action1 automation id, agent job id
  state TEXT NOT NULL, percent INTEGER NOT NULL DEFAULT 0, detail TEXT,
  requested_at TEXT NOT NULL, completed_at TEXT, last_checked_at TEXT NOT NULL);
CREATE INDEX installs_device_requested ON installs(device_id, requested_at DESC);
CREATE INDEX installs_requested ON installs(requested_at DESC);
```

Timestamps are ISO 8601 UTC strings. Ids are 32-character lower-case hex (`Guid.NewGuid().ToString("N")`), matching existing install ids.

## Steps

1. Add the package reference and `Database` service; register it as a singleton; run migrations in `Program.cs` before anything reads a store (also before the CLI branches).
2. Write migration 001 as an embedded resource; test that applying it twice is a no-op.
3. Port `CatalogStore`: `Entries`, `Find`, `Parse` keep their signatures. `Parse` still parses catalog.json; a new `Import(IReadOnlyList<CatalogEntry>)` upserts.
4. Port `DeviceStore`: same public surface. Drop the file watcher and stamp logic.
5. Port `InstallStore`: `Upsert` becomes one transaction that reads the stored row, applies the stale-write and terminal-state rules, then writes. Keep the per-device gate in `InstallService` as is.
6. First-start import and the two CLI commands.
7. Update `deploy/Dockerfile` env: `Portal__DevicesPath` stays for import only. Update `deploy/config/README.md` to say the file seeds and imports.
8. Run the smoke test; the checked-in catalog must import and `catalog verify` must pass.

## Acceptance criteria

- All existing tests pass unchanged in intent; tests that reached into JSON files are rewritten against the database.
- A data directory containing the old `devices.json` and `installs.json`, and a config volume with `catalog.json`, produces the same API responses after upgrade as before.
- Two concurrent `POST /api/v1/installs` for the same device still yield one 202 and one 409.
- A corrupt or locked database file logs an error and the process exits non-zero at start; it does not serve with empty data.
- `catalog export` output re-imports to an identical catalog.

## Verification

`dotnet test`; `deploy/smoke-test.sh` against a local image; a manual upgrade test with a copied `.scratch` data folder.

## Touches

`src/AppPortal.Server/AppPortal.Server.csproj`, `Program.cs`, new `Data/` folder (Database.cs, Migrations/001-initial.sql), `Catalog/CatalogStore.cs`, `Devices/DeviceStore.cs`, `Installs/InstallStore.cs`, `Cli/CatalogCli.cs`, `deploy/Dockerfile`, `deploy/config/README.md`, `tests/AppPortal.Server.Tests/*`.
