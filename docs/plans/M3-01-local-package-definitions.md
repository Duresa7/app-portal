# M3-01: Local package definitions in the catalog

**Milestone:** 3 (0.5.0)
**Depends on:** M1-06
**Unlocks:** M3-05

## Goal

A catalog app can carry an **agent** package: a winget id or a direct installer with silent arguments and a hash. Admins edit it on the catalog page. Nothing installs it yet; that is M3-03 and M3-04.

## Context

- `catalog_packages(app_id, engine, definition_json)` from M1-01 already allows an `agent` row. The catalog edit page (M1-06) shows a disabled Agent section.
- Games and large software use the direct form with multi-gigabyte downloads; the definition must be able to say so for the UI.

## Scope

### In
- Definition shapes (validated on save):
  - winget: `{"kind":"winget","id":"Valve.Steam","scope":"machine"|"user","version":null,"extraArgs":null,"requiresReboot":false}`
  - direct: `{"kind":"direct","url":"https://...","sha256":"...","installerType":"msi"|"exe"|"msix","silentArgs":"/qn"|"/S"|...,"sizeBytes":123456789,"uninstallKey":null,"scope":"machine"|"user","requiresReboot":false}`
- `scope` and `requiresReboot` sit on the base record so that an executor reads both without caring which kind it holds. Nothing acts on them here: M3-07 runs a user-scope install in a session, and M3-09 turns a restart into part of the install. Declaring them now is what stops the shape being frozen wrong.
- `uninstallKey` is optional. M3-04 already treats it as a hint and falls back to matching on the app name, and an msix has no entry under the Uninstall key at all. An msix likewise takes no command line, so `silentArgs` is required only for `msi` and `exe`.
- Admin page: Agent package section enabled with a kind selector and the fields above, a "Fetch and hash" helper for direct URLs that downloads server-side up to a configurable size limit (default 2 GB) and fills `sha256` and `sizeBytes`, and a winget lookup helper that queries `https://api.winget.run` or the GitHub `winget-pkgs` manifest path to confirm the id exists (best effort, network permitting).
- Device API: `CatalogApp` gains `Engines: string[]` listing which engines have definitions, and `DownloadSizeBytes: long?` for the card to show a size on large apps. Apps are still served regardless of the device's engines; M3-05 decides visibility.
- Import and export formats extended with an `agent` object beside `action1`. `catalog verify` also validates agent definitions (shape and hash format; the winget check is best effort).

### Out
- Running anything on a device. Engine selection.

## Interface

Definition JSON shapes above, frozen for M3-03, M3-04, M3-07, M3-09 and M3-11. `CatalogApp(..., string[] Engines, long? DownloadSizeBytes)`.

## Steps

1. Definition records in `AppPortal.Shared/Packages.cs` with `System.Text.Json` polymorphism on `kind`; validation; tests.
2. Store and import/export changes; tests round-trip.
3. Admin page section and helpers.
4. Contract change; client shows the size on the card when present.

## Acceptance criteria

- An app with only an agent package is saved, exported, re-imported identically, and served with `Engines == ["agent"]`.
- A per-user definition and a definition needing a restart survive the same round trip with both fields intact.
- Bad definitions (missing sha256 on direct, unknown kind, a scope that is neither machine nor user) are rejected on the page with a message.

## Verification

`dotnet test`; manual page check.

## Touches

`src/AppPortal.Shared/Packages.cs` (new), `Contracts.cs`, `src/AppPortal.Server/Catalog/*`, `Pages/Admin/Catalog/Edit.cshtml*`, `Cli/CatalogCli.cs`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/CatalogStoreTests.cs`, `tests/AppPortal.Server.Tests/PackageDefinitionTests.cs`.
