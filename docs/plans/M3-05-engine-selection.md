# M3-05: Engine selection and labels

**Milestone:** 3 (0.5.0)
**Depends on:** M3-01, M3-02, M1-07
**Unlocks:** M3-06

## Goal

When an app can be installed by more than one engine on a device, a server-wide preference decides, each app can override it, and every install is labelled with the engine that ran it in the client and the admin UI.

## Context

- `catalog_apps.engine_override` (M1-01), `devices.engine_preference` (M1-01), `EngineLabel` (M1-07), `IInstallEngine` selection stub in `InstallService` (M3-02).
- Rule, in order: the device's `engine_preference` if set and available; else the app's `engine_override` if set and available; else the server default from `settings`; else whichever single engine is available. "Available" means the app has a definition for that engine and the device can use it (`action1_endpoint_id` present, or `has_agent`). No available engine means the app is hidden on that device.

## Scope

### In
- Migration 010: `settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)` with `default_engine` (`action1` or `agent`, default `action1`).
- `/admin/settings` page with the default engine and a short explanation of the rule. Catalog edit page gains the engine override selector. Device detail page already has the per-device preference (M1-09); label it consistently.
- Device API: `GET /api/v1/catalog` filters apps with no available engine for the calling device and returns `Engine` (the one that would run) on each `CatalogApp`. `InstallRequest.Engine` is returned and shown.
- Client: card shows "via Action1" or "via Agent" in small text under the install button; Activity rows show the label; Installed merges both inventories.
- Admin installs page already shows the label; the dashboard adds a split of installs by engine for the last 30 days.

### Out
- Letting the user pick. Per-group defaults.

## Interface

`settings` table; `CatalogApp.Engine: string`; `InstallRequest.Engine: string`. `EngineSelector.Choose(device, app, defaultEngine) -> string?` in the server with exhaustive tests.

Two things differ from this plan as written:

- The migration is **014**. 010 through 013 were taken by the agent jobs, the device software, the job requester and the software account while this package was open.
- The dashboard split by engine is not here. The dashboard has no chart of any kind yet, and adding its first one to carry this is a bigger change than this package should make; the installs list already filters and labels by engine, which is what somebody asking the question actually uses.

## Steps

1. Selector with a table-driven test covering every combination.
2. Settings table and page.
3. Contract additions and client labels; demo mode shows a mix.
4. Catalog filter and install path use the selector.

## Acceptance criteria

- With default `action1`, an app with both definitions on a device with both engines goes through Action1; flipping the setting sends the next install through the agent; setting the app override wins over the default; the device preference wins over both.
- An app with only an agent definition is invisible on a device without the agent.

## Verification

`dotnet test`; manual run with two devices in fake mode.

## Touches

`src/AppPortal.Server/Installs/EngineSelector.cs` (new), `Installs/InstallService.cs`, `Settings/SettingsStore.cs` (new), `Pages/Admin/Settings/*` (new), `Pages/Admin/Catalog/Edit.cshtml*`, `Api/PortalEndpoints.cs`, `Data/Migrations/010-settings.sql`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/*`, `Views/*.axaml`, `Services/DemoPortalApiClient.cs`, `tests/AppPortal.Server.Tests/EngineSelectorTests.cs`.
