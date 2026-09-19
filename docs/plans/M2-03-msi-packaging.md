# M2-03: MSI packaging of client and agent

**Milestone:** 2 (0.4.0)
**Depends on:** M2-02
**Unlocks:** M2-04, M2-05, M2-06

## Goal

One MSI installs the client and the agent to Program Files, registers the service, writes the configuration from two public properties, and upgrades cleanly over itself. Group Policy, Intune and RMMs can deploy it silently.

## Context

- Today: `Install-AppPortalClient.ps1` copies files, writes `client.json` with ACLs, adds a Start menu shortcut and an uninstall entry, and registers the scheduled task. All of that becomes MSI authoring; the script and the scheduled task retire.
- WiX v5 (`wix` .NET tool, `WixToolset.Sdk`) builds MSIs with `dotnet build` on Windows; the CI Windows runner does this. Use a fixed `UpgradeCode`; `ProductVersion` from `Directory.Build.props`; `MajorUpgrade` with `Schedule="afterInstallExecute"` so files are replaced in place and the service restarts.
- Enrollment happens at first agent start, not in a custom action: the MSI writes `serverUrl` and `enrollmentKey` into `%ProgramData%\AppPortal\enroll.json` (Administrators only); the agent consumes it, calls `POST /api/v1/enroll`, writes `client.json`, deletes `enroll.json`. That keeps the MSI free of custom actions and keeps the key out of the MSI log. If `client.json` already exists with a token, `enroll.json` is ignored and removed.

## Scope

### In
- `src/AppPortal.Installer/AppPortal.Installer.wixproj` producing `AppPortal-<version>-x64.msi`. Components: client files, agent exe, ServiceInstall and ServiceControl for `AppPortalAgent`, Start menu shortcut for all users, ARP entry with icon, `%ProgramData%\AppPortal` folder with ACLs (Users read, Administrators full), `enroll.json` written from properties `SERVERURL` and `ENROLLMENTKEY` when both are given.
- Properties: `SERVERURL`, `ENROLLMENTKEY`, `ACTION1ENDPOINTID` (optional, passed into `enroll.json`).
- Silent install: `msiexec /i AppPortal-0.4.0-x64.msi /qn SERVERURL=https://... ENROLLMENTKEY=ape_...`. Silent upgrade: the same without properties. Uninstall removes files and the service, keeps `%ProgramData%\AppPortal` unless `REMOVEDATA=1`.
- Agent: first-start enrollment consuming `enroll.json` (this is agent code and belongs here because the MSI contract defines it).
- CI: build the MSI on the Windows runner, upload as an artifact, attach to releases alongside the zip. The zip keeps shipping for one release with a deprecation note.

### Out
- The interactive bootstrapper (M2-05). Code signing. Per-user installs.

## Interface

- MSI file name `AppPortal-<version>-x64.msi`; `UpgradeCode` recorded in the wixproj and in this file once chosen.
- Properties above. `enroll.json` shape: `{"serverUrl":"...","enrollmentKey":"...","action1EndpointId":null}`.
- Install directory `%ProgramFiles%\App Portal`, unchanged, so AppLocker rules keep matching.

## Steps

1. wixproj, product authoring, harvesting the published client folder at build time.
2. Agent first-start enrollment with tests using a temp `ProgramData` path.
3. CI job on Windows: publish client and agent, build MSI, upload.
4. Release job attaches the MSI; README gets a short "Deploy the MSI" section replacing the PowerShell instructions; the PowerShell scripts are removed.

## Acceptance criteria

- On a clean Windows 11 VM: silent install with properties, service running, device enrolled within one minute, client launches from the Start menu, ARP shows the version.
- Silent upgrade from the previous MSI keeps the token and history; the service comes back.
- Uninstall leaves no files in Program Files and no service.

## Verification

Manual VM matrix (install, upgrade, uninstall); CI installs the MSI silently on the runner and checks the service exists (M2-06 formalises this).

## Touches

`src/AppPortal.Installer/**` (new), `src/AppPortal.Agent/Enrollment/*` (new), `.github/workflows/ci.yml`, `README.md`, `deploy/windows/*` (removed), `AppPortal.sln`, `tests/AppPortal.Agent.Tests/EnrollmentTests.cs`.
