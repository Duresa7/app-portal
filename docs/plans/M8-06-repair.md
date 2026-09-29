# M8-06: Repair

**Milestone:** 8 (0.11.0)
**Depends on:** M8-01, M8-02, M8-05
**Unlocks:** M8-08

## Goal

A person can repair a catalog app that is installed but broken, from the client, and an administrator can do the same from the device page. Each kind of package is repaired the way its own tools repair it; a Steam game has its files checked by Steam.

## Context

- "Repairing an install in place" has been on the Deferred list since milestone 3.
- Each source has its own repair:
  - winget 1.8 and later: `winget repair --id <id> --exact --silent` runs the package's own repair (an MSI's repair, an MSIX re-registration, or an installer's repair switch).
  - A direct MSI with a product code in `UninstallKey`: `msiexec /fa <code> /qn /norestart` reinstalls every file.
  - A direct exe or MSIX, and a portable app: install it again from the cached, verified file.
  - Steam: `steam://validate/<appid>` makes Steam check and replace the game's files, in the person's session.
  - Package managers and the other launchers have no repair the portal can drive. They say so.
- M8-05 added a third job kind, `update`, and the default-method pattern on `IPackageExecutor`. Repair is a fourth kind on the same path.

## Scope

### In

- **`InstallKind.Repair`** = `repair`. Pages say "Repair" and "Repaired".
- **Contracts:** `CreateRepairRequest(string AppId)` on `POST /api/v1/repairs`, and `POST /api/v1/admin/devices/{id}/repairs` with `AdminRepairRequest(string AppId, string? Account)`.
- **Service:** `InstallService.RepairAsync`: the app must be installed on the device (a succeeded install of it, or a matching inventory row), must have an agent package, and nothing may be in progress for it. A person may repair what they may install.
- **Agent:** `IPackageExecutor.RepairAsync`, a default method that returns "This kind of package cannot be repaired from the portal."
  - `WingetExecutor`: `winget repair ...` as SYSTEM or in the session. An old winget without `repair` fails with "This PC's winget is too old to repair apps. Update App Installer."
  - `DirectInstallerExecutor`: `msiexec /fa` for an MSI with a product code; otherwise the install command again.
  - `PortableAppExecutor`: the install script again.
  - `LauncherHandoffExecutor`: `steam://validate/<appid>` for Steam, with "Opened in Steam. Steam is checking the game's files."; the others fail with "Repair it in {Launcher}."
- **Client:** a Repair button on an installed catalog app, on the Installed page and on the app card's installed state.
- **Admin:** a Repair button on the installed catalog apps of the device page, web and client.
- **Docs:** `docs/administration.md`, `docs/api.md`.
- **Roadmap:** remove "repairing an install in place" from Deferred.

### Out

- Repair through Action1 or Intune. The engine that owns the install owns its repair.

## Steps

1. Tests first: the service rules, `JobRunner` routing, and each executor's command and messages.
2. Service, endpoints, executors.
3. Client and admin buttons, docs.

## Acceptance criteria

- A winget app, a direct MSI, a direct exe, a portable app and a Steam game can each be repaired from the client and from the admin device page, and the history shows a Repair row with its result.
- A package-manager app, and a launcher other than Steam, give their sentence and change nothing.
- Format, build and all tests pass on Linux and on the Windows leg.

## Touches

`src/AppPortal.Shared/{EngineLabel,Contracts,AdminContracts}.cs`, `src/AppPortal.Agent/Jobs/{IPackageExecutor,JobRunner}.cs`, `src/AppPortal.Agent/Executors/{WingetExecutor,DirectInstallerExecutor,PortableAppExecutor,LauncherHandoffExecutor}.cs`, `src/AppPortal.Server/Installs/{InstallService,AgentInstallEngine}.cs`, `src/AppPortal.Server/Api/PortalEndpoints.cs`, `src/AppPortal.Server/Admin/Api/AdminDeviceEndpoints.cs`, `src/AppPortal.Server/Pages/Admin/Devices/Detail.cshtml*`, client view models, views and services, `docs/*.md`, `docs/ROADMAP.md`, tests.
