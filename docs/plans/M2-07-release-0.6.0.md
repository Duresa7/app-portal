# M2-07: Release 0.6.0

**Milestone:** 2 (0.6.0)
**Depends on:** M2-04, M2-06
**Unlocks:** none directly (M3 and M4 depend on earlier packages)

## Goal

Ship the agent, the MSI, and the wizard through the gate, with a migration path for 0.3.x devices installed from the zip.

## Scope

### In
- README: "Client deployment" becomes "Install on PCs" with three paths: the wizard for a tech, the MSI with properties for Group Policy, Intune and RMMs, and silent `AppPortalSetup.exe`. "Updates" describes the agent. Remove every mention of the PowerShell scripts and the scheduled task. Repository layout table updated for the new projects.
- Migration from zip installs: document that the last zip-based release's updater will not install an MSI; 0.3.x devices are moved by deploying the 0.6.0 MSI once through the same channel the zip went through. The MSI removes the scheduled task and reuses `client.json`.
- Zip artifact retired from the release; the gate's file checks updated.
- Bump to 0.6.0, tag after green.

### Out
- Feature work.
- Screenshots of the wizard. They were in this package's scope and are dropped: they need somebody at a Windows machine to run the wizard and look at it, and nothing in the release depends on them.

## Acceptance criteria

- Tag run green, release carries `AppPortal-0.6.0-x64.msi`, `AppPortalSetup.exe`, `SHA256SUMS`, and the server image is pushed.
- A 0.3.x VM upgraded via the MSI keeps its token and shows the agent heartbeat.
- Roadmap rows M2-01 through M2-07 Done.

## Touches

`README.md`, `docs/images/*`, `docs/ROADMAP.md`, `.github/workflows/ci.yml`, `Directory.Build.props`.
