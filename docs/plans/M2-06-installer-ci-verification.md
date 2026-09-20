# M2-06: Installer verification in CI

**Milestone:** 2 (0.6.0)
**Depends on:** M2-03, M2-05
**Unlocks:** M2-07

## Goal

The release gate proves the MSI and the bootstrapper install, enroll, and uninstall on a real Windows runner against a real server started in the job. A broken installer cannot ship.

## Context

- The Windows runner cannot run Linux containers, so the server runs as a `dotnet` process from the build output with `Action1__Mode=Fake` and a temp data directory.
- The current `client-verify` job checks the zip; this package replaces it with installer checks and keeps the demo-mode launch.

## Scope

### In
- New job `installer-verify` on `windows-latest`, needs the MSI and setup artifacts: start the server, create an admin and an enrollment key through the CLI, run `AppPortalSetup.exe /quiet /server http://127.0.0.1:5080 /key <key>`, assert exit 0, assert the service `AppPortalAgent` is running, assert `client.json` exists with a token, assert `GET /admin/devices` (with an admin session) lists the runner's device with a heartbeat, launch the installed client in demo screenshot mode, run `msiexec /x` silently, assert files and service are gone.
- Upgrade check: install the previous release's MSI (downloaded from the latest GitHub release) first, then run the freshly built MSI over it, assert the token and version. Skip when no previous MSI exists yet.
- MSI static checks: `ProductVersion` equals the props version; `UpgradeCode` equals the recorded constant; the file list includes `AppPortal.exe`, `AppPortal.Agent.exe`.
- Release job requires this job.

### Out
- Signing. Testing on Windows 10 runners (document as manual).

## Interface

`.github/workflows/ci.yml` job names: `installer-verify` replaces `client-verify` in `release.needs`.

## Steps

1. PowerShell script `deploy/windows/ci-installer-test.ps1` doing the steps so it can be run on a VM by hand too.
2. Workflow job wiring, artifacts of logs and screenshot on failure.
3. Upgrade path once a released MSI exists.

## Acceptance criteria

- A deliberate break (remove the ServiceInstall element in a scratch branch) fails the job with a readable message.
- The job runs in under ten minutes.

## Verification

Run the workflow on a branch; break it on purpose once.

## Touches

`.github/workflows/ci.yml`, `deploy/windows/ci-installer-test.ps1` (new), `README.md` (Releasing section).
