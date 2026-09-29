# M8-05: Updates

**Milestone:** 8 (0.11.0)
**Depends on:** M3-03, M5-01, M7-02
**Unlocks:** M8-06, M8-08

## Goal

The portal shows which installed software has a newer version, and a person can update a catalog app from the client with one click. An administrator sees every available update on a device, in the web admin and in the client, and can start one there too. Updates go through winget, for apps from winget and from the Microsoft Store.

## Context

- winget knows what it can update: `winget upgrade` lists every package with a newer version in its sources, as a table with the columns Name, Id, Version, Available and Source. `winget upgrade --id <id> --exact --silent` installs the newer version.
- The agent already sweeps winget's installed list as SYSTEM for the machine and in each person's session for their profile: at service start, after each install, and at each sign-in (M7-02). The update list is read at the same moments, in the same places, and once a day for the machine.
- `WingetExecutor` maps winget's exit codes. `0x8A150011` is "no applicable update", which for an update means it is already up to date.
- Installs and removals are rows in one history with a `kind` column (migration 018): `install` and `uninstall`. An update is a third kind, so every history page shows it without being taught anything new.

## Scope

### In

- **`InstallKind.Update`** = `update`, in `src/AppPortal.Shared/EngineLabel.cs`. Pages and the client say "Update" and "Updated".
- **Agent list:** `SoftwareReporter.ReportUpdatesAsync(settings, ct, account)` runs `winget upgrade --accept-source-agreements --disable-interactivity` as SYSTEM or in the person's session, parses it with a new `WingetUpgradeList.Parse` (Name, Id, Version, Available; rows with an unknown version are kept), and posts it. It runs after the software list in `StartupSoftwareSweep`, `SignInSoftwareSweep` and after each successful job, and once a day for the machine from a new `DailyUpdateSweep` hosted service. An unreadable table leaves the last report alone, as the software list does.
- **Route** `POST /api/v1/agent/updates?account=` with `[{ "name", "id", "version", "available" }]`, at most 1000 rows, replacing that device's list for that account. **Table** `device_updates (device_id, account, name, package_id, version, available, seen_at)`, migration 024.
- **Installed list:** `InstalledApp` gains `string? AvailableVersion = null` and `bool CanUpdate = false`. A row gets the available version by package id when its catalog app is a winget or Store package with that id, and by name otherwise. `CanUpdate` is true when the row matches a catalog app whose agent package is winget or Store, the device has the agent, and nothing is in progress for that app.
- **Device API:** `POST /api/v1/updates` with `CreateUpdateRequest(string AppId)`. `InstallService.UpdateAsync` creates an install row of kind `update` on the agent engine and a job of the same kind. A per-user package is updated for the person who asks. It refuses an app that is not a winget or Store package, an app with something already in progress, and a device without the agent.
- **Agent job:** `IPackageExecutor` gains `UpdateAsync`, a default interface method that returns "This kind of package cannot be updated from the portal." `JobRunner` sends a job of kind `update` to it. `WingetExecutor.UpdateAsync` runs `winget upgrade --id <id> --exact --source <source> --silent --accept-package-agreements --accept-source-agreements --disable-interactivity`, as SYSTEM or in the session, with the same exit-code map; `0x8A150011` succeeds with "Already up to date."
- **Client:** the Installed page shows "Update to {version}" on a row that `CanUpdate`, and a plain "{version} available" on one that cannot; the Apps card of an installed app with an update shows "Update available" and an Update button.
- **Admin:** the device detail page lists the device's available updates, in the web admin and in the client, with an Update button on the rows a catalog app covers. `POST /api/v1/admin/devices/{id}/updates` with `AdminUpdateRequest(string AppId, string? Account)`. `AdminDeviceDetail` gains `IReadOnlyList<AdminAvailableUpdate>? Updates`.
- **Docs:** `docs/administration.md`, `docs/api.md`, `docs/how-it-works.md`.

### Out

- Updating software that no catalog app covers. It is listed, not updated.
- Updating direct, portable, launcher and package-manager apps. A direct or portable app is updated by changing its definition and installing it again.
- Automatic updates on a schedule.

## Interface

```csharp
public sealed record AvailableUpdate(string Name, string Id, string Version, string Available);
public sealed record CreateUpdateRequest(string AppId);
public sealed record AdminUpdateRequest(string AppId, string? Account = null);
public sealed record AdminAvailableUpdate(string Name, string Version, string Available, string? Account, string? CatalogAppId, bool CanUpdate);
```

Route constant: `ApiRoutes.Updates = Prefix + "/updates"`.

## Steps

1. Tests first: `WingetUpgradeList.Parse` against real `winget upgrade` output captured on Windows (with the Store notice, a truncated name, "Unknown" versions, and the footer lines); the endpoint; the store; `InstalledAppsAsync` matching by id and by name; `UpdateAsync` rules; `JobRunner` routing; `WingetExecutor.UpdateAsync` arguments and exit codes.
2. Migration, store, endpoints, service.
3. Agent list and job.
4. Client and admin pages, contracts, demo data, docs.

## Acceptance criteria

- On a PC with an out-of-date winget package that the catalog covers, the Installed page shows "Update to {version}", pressing it updates the app, and the row then shows no update.
- The same update started from the device page in the web admin and in the client works the same way.
- Format, build and all tests pass on Linux and on the Windows leg.

## Verification

Tests as above. The parser's test data is real output from `winget upgrade` on a Windows 11 PC, recorded in the test with the date.

## Touches

`src/AppPortal.Shared/{EngineLabel,Contracts,AdminContracts}.cs`, `src/AppPortal.Agent/Executors/{WingetExecutor,WingetUpgradeList}.cs`, `src/AppPortal.Agent/Jobs/{IPackageExecutor,JobRunner,SoftwareReporter,StartupSoftwareSweep,SignInSoftwareSweep,DailyUpdateSweep}.cs`, `src/AppPortal.Agent/AgentRun.cs`, `src/AppPortal.Server/Data/Migrations/024-device-updates.sql` (new), `src/AppPortal.Server/Devices/DeviceUpdateStore.cs` (new), `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `src/AppPortal.Server/Api/PortalEndpoints.cs`, `src/AppPortal.Server/Installs/{InstallService,AgentInstallEngine}.cs`, `src/AppPortal.Server/Admin/Api/AdminDeviceEndpoints.cs`, `src/AppPortal.Server/Pages/Admin/Devices/Detail.cshtml*`, `src/AppPortal.Server/Program.cs`, client view models, views and services for the Installed page, the app card and the device page, `docs/*.md`, tests.
