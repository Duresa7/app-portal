# M3-03: winget executor

**Milestone:** 3 (0.5.0)
**Depends on:** M3-02
**Unlocks:** M3-06, M3-07, M3-11

## Goal

The agent installs winget packages machine-wide as SYSTEM and reports progress and outcome through the job protocol.

## Context

- winget is not on the PATH for SYSTEM. The executable lives under `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe`; SYSTEM can run it directly, but the first run in that context may need the package's dependencies registered. The alternative is the `Microsoft.WinGet.Client` PowerShell module. Prefer the direct executable with a fallback to the module; document what worked on Windows 10 22H2 and Windows 11.
- winget prints progress to stdout as a redrawn bar; parse the percentage when possible and otherwise report phase changes only.
- Exit codes: 0 success, `0x8A150011` already installed treated as success, `-1978335189` no applicable installer, others failed with the tail of stdout as detail.

## Scope

### In
- `WingetExecutor : IPackageExecutor` for `kind == "winget"`: locate winget, run `winget install --id <id> --exact --scope <scope> --silent --accept-package-agreements --accept-source-agreements --disable-interactivity [--version v] [extraArgs]`, with the scope taken from the definition rather than fixed, stream output to the job log, map exit codes, report progress.
- Source update once per day (`winget source update`) before the first install of the day, with a timeout.
- Job log file per job under `%ProgramData%\AppPortal\jobs\<id>.log`, its tail sent as detail on failure.
- `JobRunner` is registered as a hosted service. M3-02 built it and tested it but never added it to the host, so an agent would never have asked for a job. It stays out of a `--console --once` run, whose only purpose is one heartbeat and which should not wait out a twenty-five second long poll before exiting.
- Installed-software reporting: the agent runs `winget list` after a success and posts the found display name and version with the completion so `GET /api/v1/device/installed` can show agent-installed apps (server side: store them in `device_software(device_id, name, version, seen_at)` via migration 009 and merge with Action1 inventory when both exist).

### Out
- Uninstall; that is M3-11. Upgrades of already-installed apps.
- Running a user-scope install inside somebody's session; that is M3-07. This package passes the scope through and carries out machine scope. A user-scope job fails here with "This package installs for one person, and the agent cannot yet run in a user session", and M3-07 replaces that failure with the real path.

## Interface

`device_software` table and `POST /api/v1/agent/software` `[{name, version}]` replacing the device's list. Completion body from M3-02 unchanged.

Three things differ from this plan as written, and the code is what shipped:

- The migration is **011**, not 009. Enrollment took 009 and the agent jobs took 010 while this package was open.
- `IPackageExecutor.RunAsync` gains a `jobId` first parameter. The plan asks for a log file per job and the interface it inherited had no way to name one.
- The software report is a separate call after the completion, not a field on it. The agent sends its whole list so that software somebody removed can leave the record, which a per-install field could never express.

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
