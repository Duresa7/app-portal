# M2-04: Agent self-update via MSI

**Milestone:** 2 (0.6.0)
**Depends on:** M2-03
**Unlocks:** M2-07

## Goal

The agent keeps the client and itself current by downloading the newest release MSI, verifying it, and running it silently. The scheduled-task updater and the rename swap retire.

## Context

- Today's updater logic is in `src/AppPortal.Updater` (`ReleaseFeed`, `Installer`, `UpdateRun`) and is documented under "Updates" in the README: GitHub releases feed, `SHA256SUMS` verification, staged swap, `update.json` status consumed by the client's banners (`UpdateStatusReader`).
- With an MSI, Windows Installer replaces files and restarts the service; a running client blocks in-use files, so the MSI upgrade must be scheduled for when the client is closed, which is the same "Restart to update" hand-off the client already has.

## Scope

### In
- Agent update loop: at start, daily at a random time between noon and one, and when the client asks (see below), fetch the latest release, compare with the installed version, download `AppPortal-<version>-x64.msi`, verify against `SHA256SUMS`, and store it under `%ProgramData%\AppPortal\updates\`.
- Apply when no client from the install folder is running: `msiexec /i <msi> /qn /norestart /l*v update-<version>.log`, run as SYSTEM by the agent itself. After success delete older MSIs. On failure keep the log and report in `update.json`.
- `update.json` keeps its shape so the client's banners keep working: `available` when downloaded and a client is running, `ready` is no longer used, `installed`, `failed`, `upToDate`, `offline`.
- The client's "Update now" and "Restart to update" buttons stop starting a scheduled task and instead write a request file `%ProgramData%\AppPortal\update.request` (Users write, agent watches) and exit when asked; the agent picks it up within 10 seconds.
- Remove `src/AppPortal.Updater` and its tests; move `ReleaseFeed` and `VersionText` usages into the agent; keep the tests that still apply.
- The MSI upgrade of a 0.3.x install must remove the old scheduled task: the MSI authoring in M2-03 gets a `RemoveScheduledTask` custom action-free equivalent by having the agent delete the task on first start if present.

### Out
- Rollback. Update channels. Updating from anywhere other than GitHub releases (the `updateRepository` override stays).

## Interface

- `update.json` shape unchanged from `AppPortal.Shared/UpdateStatus.cs`.
- `update.request` file; `updates/` folder; `msiexec` log naming.

## Steps

1. Port feed and verification into the agent; unit tests with a fake feed.
2. Apply logic with the client-running check moved from `Installer.ClientIsRunning`.
3. Client button changes; demo mode unaffected.
4. Delete the updater project; update README "Updates" and the release gate's required file list in `ci.yml` (no `AppPortal.Updater.exe`).

## Acceptance criteria

- On a VM with 0.4.0 installed, publishing 0.4.1 leads to the agent applying it within a day without user action when the client is closed, and after "Restart to update" when it is open. Tested by pointing `updateRepository` at a fork with a test release.
- No scheduled task named "App Portal Updater" remains after upgrade.

One thing differs from this plan as written, and the code is what shipped.

The scheduled task was not the only thing a zip install left behind, and naming only the task is why
nobody wrote the rest. A zip install also wrote an uninstall entry of its own, `AppPortalClient` under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, and left `AppPortal.Updater.exe` and
`Uninstall-AppPortalClient.ps1` in the install folder. The entry is the one that hurts: it sits in Apps
and Features beside the MSI's under the same name, and the script behind it deletes both the install
folder and the state folder, so whoever picks the wrong row of two identical ones leaves Windows
Installer holding a product whose files are gone. Reported as
[#49](https://github.com/Duresa7/app-portal/issues/49) after two PCs upgraded in place. The agent now
removes all three on its first start, and the criterion should be read as: nothing a zip install wrote
remains after upgrade.

## Verification

`dotnet test`; manual VM test with a fork release; CI Windows job runs the agent update step in `--check` mode against the real feed to ensure parsing still works.

## Touches

`src/AppPortal.Agent/Update/**` (new), `src/AppPortal.Updater/**` (removed), `tests/AppPortal.Updater.Tests/**` (moved into `tests/AppPortal.Agent.Tests`), `src/AppPortal.Client/ViewModels/MainViewModel.cs`, `src/AppPortal.Client/Services/UpdateStatusReader.cs`, `.github/workflows/ci.yml`, `README.md`, `AppPortal.sln`.
