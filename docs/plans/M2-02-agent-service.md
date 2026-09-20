# M2-02: Agent service skeleton and heartbeat

**Milestone:** 2 (0.6.0)
**Depends on:** M1-09
**Unlocks:** M2-03, M3-02

## Goal

A Windows service, **App Portal Agent**, runs as SYSTEM on every device, reads the same `client.json` as the client, and reports to the server on a schedule. It does nothing else yet; updates arrive in M2-04 and installs in M3-02.

## Context

- New project `src/AppPortal.Agent`, a .NET Worker Service with `Microsoft.Extensions.Hosting.WindowsServices`. Published self-contained win-x64, single file, like the updater today.
- `client.json` lives in `%ProgramData%\AppPortal\` (`ClientSettings.Load` in the client shows the shape: `serverUrl`, `deviceToken`, `refreshSeconds`, optional `updateRepository`). The agent reads it; it does not own it.
- The agent uses the device token. That is acceptable: the token already sits in a file readable by every user on the device.

## Scope

### In
- Service host with a `--console` flag for running in a terminal, `--install` and `--uninstall` helpers that register the service (used by the MSI custom action-free approach: the MSI's ServiceInstall element does it; the flags exist for development).
- Heartbeat every 15 minutes and at start: `POST /api/v1/agent/heartbeat {agentVersion, clientVersion, osVersion}`; server updates `devices.agent_version`, `last_seen_at`, and sets `has_agent = 1` if it was 0. Answer `{serverTime, heartbeatSeconds}` so the interval can be tuned server-side.
- Logging to `%ProgramData%\AppPortal\agent.log` with size-based rotation at 5 MB, keep 3.
- Status file `%ProgramData%\AppPortal\agent.json` with last heartbeat time and outcome, readable by users, for the client's diagnostics later.
- Tests for the heartbeat client with a fake handler and for log rotation.

### Out
- Updating anything. Installing anything. Any UI.

## Interface

- Route `POST /api/v1/agent/heartbeat`, device bearer auth, body and answer above.
- Files: `%ProgramData%\AppPortal\agent.log`, `agent.json`.
- Service name `AppPortalAgent`, display name "App Portal Agent", start automatic (delayed), recovery restart after 1 minute.

## Steps

1. Project, host, settings reader shared with the client by moving `ClientSettings` into `AppPortal.Shared` (rename `PortalSettings`), keep the client compiling.
2. Heartbeat loop with jitter; server endpoint and store update; tests.
3. Publish profile and inclusion in the CI client artifact as `client/AppPortal.Agent.exe` (the MSI in M2-03 replaces this arrangement).

## Acceptance criteria

- Running `AppPortal.Agent.exe --console` on a developer machine against the fake-mode server records a heartbeat visible on `/admin/devices`.
- The service installs, starts, and survives a reboot on a Windows 11 VM.

## Verification

`dotnet test`; manual VM run; CI Windows job runs the agent with `--console --once` and checks the exit code.

## Touches

`src/AppPortal.Agent/**` (new), `src/AppPortal.Shared/PortalSettings.cs` (moved), `src/AppPortal.Client/Services/ClientSettings.cs` (thin wrapper or removed), `src/AppPortal.Server/Agent/AgentEndpoints.cs` (new), `Devices/DeviceStore.cs`, `AppPortal.sln`, `.github/workflows/ci.yml`, `tests/AppPortal.Agent.Tests/**` (new).
