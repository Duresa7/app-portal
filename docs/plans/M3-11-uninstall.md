# M3-11: Taking software off again

**Milestone:** 3 (0.5.0)
**Depends on:** M3-03, M3-04, M3-07

**Unlocks:** M3-06

## Goal

Software the agent installed can be removed through the agent, by the person who installed it when the administrator allows it, and by an administrator always.

## Context

- Both executor packages list uninstall as out of scope, so the portal is a one-way door. Every removal is a ticket, which is the thing this product exists to avoid.
- Removal matters most for the software that was hardest to install. Anti-cheat drivers, agents and anything else that loads at boot are the cases where somebody wants it gone, and they are exactly the cases where a person cannot do it themselves from the Settings app without help.
- The install record already knows the engine, the definition and the account, so a removal knows how the software arrived. Nothing else has to be guessed.

## Scope

### In
- Migration 015: `catalog_apps` gains `user_removable INTEGER NOT NULL DEFAULT 0`. `installs.kind TEXT NOT NULL DEFAULT 'install'` with values `install` and `uninstall`, so a removal is an ordinary row in the same history, with the same states and the same progress.
- Device API: `POST /api/v1/uninstalls {appId}`. Allowed when the app is `user_removable` and the requester is the account the install was made for, or for any machine-wide install of a `user_removable` app. Otherwise 403 with a reason the client shows.
- Admin: a Remove button on the device detail page and on the install row, which raises the same uninstall against that device without the `user_removable` check.
- `IPackageExecutor` gains `UninstallAsync`. winget runs `winget uninstall --id <id> --exact --silent --disable-interactivity` with the scope the install used. A direct `msi` runs `msiexec /x` with the product code from the uninstall key. A direct `exe` runs the `QuietUninstallString` from the uninstall key, and fails with a clear detail when the key has only an interactive `UninstallString`, because a silent removal that opens a window on somebody's screen is worse than an honest failure. A direct `msix` runs `Remove-AppxPackage` in the session for user scope and `Remove-AppxProvisionedPackage` for machine scope.
- A user-scope removal runs in that account's session through the M3-07 launcher, and parks the same way when the account is not signed in.
- A removal that needs a restart uses the M3-09 states unchanged.
- Inventory is updated after a removal, and the catalog card goes back to offering the install.
- Prerequisites are not removed with the app that needed them. Something else may want them, and guessing is worse than leaving them.

### Out
- Removing software the portal did not install. Removing Action1 deployments; Action1 owns those. Repairing an install; a removal and an install do it.

## Interface

`catalog_apps.user_removable`, `installs.kind`. Route `POST /api/v1/uninstalls` with body `{appId}`. `IPackageExecutor.UninstallAsync(PackageDefinition d, IProgress<...> p, CancellationToken ct)`. `InstallRequest.Kind: string`.

## Steps

1. Migration, permission rule and its tests, covering each of owner, other person, machine-wide and administrator.
2. `UninstallAsync` per executor with a fake process runner; uninstall key reading with fixtures.
3. Client button, confirmation and history display; admin buttons.
4. VM test: install and remove one app of each kind, machine and user scope.

## Acceptance criteria

- A person removes an app they installed, and it disappears from their Installed list and from the device.
- The same person cannot remove an app another person installed, and the refusal says why.
- An administrator removes it from the device page and the history shows both the install and the removal.
- An exe whose uninstall key offers no quiet string fails with a readable detail instead of opening a window.

## Verification

`dotnet test`; VM run.

## Touches

`src/AppPortal.Server/Installs/InstallService.cs`, `Api/PortalEndpoints.cs`, `Catalog/CatalogStore.cs`, `Pages/Admin/Devices/Detail.cshtml*`, `Pages/Admin/Catalog/Edit.cshtml*`, `Data/Migrations/015-uninstall.sql`, `src/AppPortal.Agent/Executors/*`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/*`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/UninstallTests.cs`, `tests/AppPortal.Agent.Tests/UninstallExecutorTests.cs`.
