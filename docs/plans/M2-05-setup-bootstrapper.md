# M2-05: Setup.exe bootstrapper

**Milestone:** 2 (0.6.0)
**Depends on:** M2-01, M2-03
**Unlocks:** M2-06

## Goal

A tech at the keyboard double-clicks `AppPortalSetup.exe`, enters the server URL and an enrollment key, watches the install and the enrollment succeed, and is done. Under the hood it runs the MSI.

## Context

- Avalonia, same styling as the client (`Styles/Fluent2Colors.axaml`, Windows 11 geometry). Self-contained single file with the MSI embedded as a resource so one file is all a tech carries.
- Must request elevation (`requestedExecutionLevel requireAdministrator` in the manifest).
- Validation before installing: `GET /api/v1/enroll/check` with the key (M2-01). After installing: poll `%ProgramData%\AppPortal\client.json` appearing and `agent.json` heartbeat for up to 60 seconds.

## Scope

### In
- Project `src/AppPortal.Setup`. Pages: Welcome; Server (URL field with reachability check via `/healthz`, enrollment key field with the check call, optional Action1 endpoint id shown only when the key's engine requires it, reported by the check endpoint's answer body); Installing (progress from `msiexec` exit and the enrollment poll); Done (device name as enrolled, Open App Portal button) or Failed (the reason, the MSI log path, Copy details).
- Silent mode: `AppPortalSetup.exe /quiet /server <url> /key <key> [/endpoint <id>]` runs the same steps without UI and exits with the msiexec code or 1 on enrollment failure. RMMs that prefer an exe over the MSI use this.
- Extracts the MSI to `%TEMP%`, runs `msiexec /i ... /qn /norestart /l*v`, deletes the extracted file.
- Leaves nothing installed of itself.

### Out
- Uninstall UI (ARP handles it). Repair. Localisation.

## Interface

- File name `AppPortalSetup.exe`. Exit codes: 0 success, 1 enrollment failed, 2 invalid arguments, otherwise the msiexec code.
- `GET /api/v1/enroll/check` answer body extended in M2-01 style: `{engine: "action1"|"agent"|"both"}` so the UI knows whether to ask for the endpoint id. If M2-01 shipped without this body, add it there as a small follow-up commit under this package.

## Steps

1. Project, manifest, embedded MSI wiring in the build (the Windows CI job builds the MSI first, then the setup).
2. Pages and view model; keyboard-only path works.
3. Silent mode and exit codes; tests for argument parsing and the enrollment poll with a temp folder.
4. CI: build and upload `AppPortalSetup.exe`; the release attaches it.

## Acceptance criteria

- On a clean VM, the wizard path ends with a device on `/admin/devices` and the client opening, in under two minutes on a normal network.
- Wrong key shows a clear message before anything is installed.
- Silent mode from an elevated prompt behaves the same and returns 0.

## Verification

Manual VM; CI Windows job runs silent mode against a server started in the job (M2-06).

## Touches

`src/AppPortal.Setup/**` (new), `src/AppPortal.Server/Enrollment/EnrollmentEndpoints.cs` (check body if needed), `.github/workflows/ci.yml`, `README.md`, `AppPortal.sln`, `tests/AppPortal.Setup.Tests/**` (new).
