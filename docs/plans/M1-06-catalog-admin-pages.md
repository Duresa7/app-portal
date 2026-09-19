# M1-06: Catalog management pages

**Milestone:** 1 (0.3.0)
**Depends on:** M1-03
**Unlocks:** M1-10, M3-01

## Goal

Admins add, edit, disable and remove catalog apps in the browser instead of editing a JSON file. The Action1 package definition is edited here; the schema already has room for the agent package added in M3-01.

## Context

- `catalog_apps` and `catalog_packages` come from M1-01. `CatalogStore.Entries` is what the device API serves; it must reflect edits immediately.
- `catalog verify` resolves Action1 packages through `IAction1Client.ResolvePackageVersionAsync`; the fake client resolves anything not containing "missing".
- `IAction1Client.SearchPackagesAsync(nameFilter)` exists and backs the `packages search` CLI.

## Scope

### In
- `/admin/catalog`: table of apps (icon, name, publisher, category, featured, engines defined, hidden flag) with search. Buttons: New app, Import JSON, Export JSON.
- `/admin/catalog/{id}`: form with the fields of `catalog_apps`, the `match` rule (name contains), and an **Action1 package** section: package id, version (`latest` or explicit), a Search button calling `SearchPackagesAsync` and a Verify button calling `ResolvePackageVersionAsync`, both via htmx. An **Agent package** section is rendered disabled with the text "Available in 0.5.0".
- Migration 004: add `hidden INTEGER NOT NULL DEFAULT 0` to `catalog_apps`. Hidden apps are not served to devices but keep their history.
- Delete asks for confirmation and refuses when installs reference the app; offer Hide instead.
- Import uploads a file in the checked-in format and upserts; export downloads the same format.
- Every change updates `updated_at`; `CatalogStore` reads through to the database, no cache invalidation to get wrong.

### Out
- Agent package fields (M3-01). Engine override field UI (M3-05). Icons upload; icon stays a URL.

## Interface

- Route names above. `ViewData["Nav"] = "catalog"`.
- `CatalogStore` gains `Upsert(CatalogEntry)`, `SetHidden(id, bool)`, `Delete(id)` returning false when referenced, `Search(term)`.
- The device API filters `hidden = 0`.

## Steps

1. Migration and store methods with tests.
2. List page, edit page, new page, validation messages inline.
3. Search and Verify partials against `IAction1Client`; when `Action1:Mode` is Fake, they behave per the fake client.
4. Import and export handlers reusing `CatalogStore.Parse` and the export formatter from M1-01.
5. Tests: create through the page appears in `GET /api/v1/catalog`; hidden disappears; delete refused with history.

## Acceptance criteria

- The checked-in `catalog.json` round-trips: import, export, diff is empty apart from key order.
- An app edited in the browser is served to the client on its next refresh without restart.

## Verification

`dotnet test`; smoke test extended with a catalog edit via the admin session and a check that `GET /api/v1/catalog` reflects it.

## Touches

`src/AppPortal.Server/Pages/Admin/Catalog/*`, `Catalog/CatalogStore.cs`, `Api/PortalEndpoints.cs` (hidden filter), `Data/Migrations/004-catalog-hidden.sql`, `deploy/smoke-test.sh`, `tests/AppPortal.Server.Tests/CatalogStoreTests.cs`, `tests/AppPortal.Server.Tests/AdminCatalogPageTests.cs`.
