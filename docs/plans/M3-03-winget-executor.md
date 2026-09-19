# M3-03: winget executor

**Milestone:** 3 (0.5.0)
**Depends on:** M3-02
**Unlocks:** M3-06

## Goal

The agent installs winget packages machine-wide as SYSTEM and reports progress and outcome through the job protocol.

## Context

- winget is not on the PATH for SYSTEM. The executable lives under `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe`; SYSTEM can run it directly, but the first run in that context may need the package's dependencies registered. The alternative is the `Microsoft.WinGet.Client` PowerShell module. Prefer the direct executable with a fallback to the module; document what worked on Windows 10 22H2 and Windows 11.
- winget prints progress to stdout as a redrawn bar; parse the percentage when possible and otherwise report phase changes only.
- Exit codes: 0 success, `0x8A150011` already installed treated as success, `-1978335189` no applicable installer, others failed with the tail of stdout as detail.

## Scope

### In
- `WingetExecutor : IPackageExecutor` for `kind == "winget"`: locate winget, run `winget install --id <id> --exact --scope machine --silent --accept-package-agreements --accept-source-agreements --disable-interactivity [--version v] [extraArgs]`, stream output to the job log, map exit codes, report progress.
- Source update once per day (`winget source update`) before the first install of the day, with a timeout.
- Job log file per job under `%ProgramData%\AppPortal\jobs\<id>.log`, last 4 KB sent as detail on failure.
- Installed-software reporting: the agent runs `winget list` after a success and posts the found display name and version with the completion so `GET /api/v1/device/installed` can show agent-installed apps (server side: store them in `device_software(device_id, name, version, seen_at)` via migration 009 and merge with Action1 inventory when both exist).

### Out
- Uninstall. Upgrades of already-installed apps. Per-user scope.

## Interface

`device_software` table and `POST /api/v1/agent/software` `[{name, version}]` replacing the device's list. Completion body from M3-02 unchanged.

## Steps

1. Locator with tests over a fake directory layout; executor with a fake process runner.
2. Output parser tests with captured winget transcripts checked in as fixtures.
3. Server-side software table and merge in the installed endpoint.
4. VM test: Steam, 7-Zip, VLC via winget as SYSTEM.

## Acceptance criteria

- A winget app installed from the client on a VM appears under Installed in the client and on `/admin/installs` as succeeded via Agent.
- An unknown id fails with a readable detail, not a hang.

## Verification

`dotnet test`; VM matrix on Windows 10 22H2 and Windows 11.

## Touches

`src/AppPortal.Agent/Executors/Winget*.cs` (new), `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `Installs/InstallService.cs` (installed merge), `Data/Migrations/009-device-software.sql`, `tests/AppPortal.Agent.Tests/WingetExecutorTests.cs`, `tests/AppPortal.Agent.Tests/Fixtures/*.txt`.
