# M8-08: Release 0.11.0

**Milestone:** 8 (0.11.0)
**Depends on:** M8-01, M8-02, M8-03, M8-04, M8-05, M8-06, M8-07

## Goal

Ship milestone 8 through the gate: portable apps, games handed to their launcher, anti-cheat on the device page, catalog packs, updates, repair and private winget sources.

## Scope

### In

- **README.** The feature list names the new kinds of software, updates and repair, and the catalog packs. The boundary sentence matches the new **Launcher content** decision.
- **Screenshots**, light and dark, where a page changed enough that the old picture is wrong: the client's Installed page with an update, and the catalog page's pack section.
- **Roadmap.**
  - Rows M8-01 to M8-08 **Done**.
  - A "Milestone 8 shipped as v0.11.0" paragraph that says what was proven on a real PC and what was proven by tests only, and names no machine. The release is not signed.
  - Review the Deferred list.
- **Version.** Bump `Directory.Build.props` to 0.11.0. Run the full gate on `main` with the Windows jobs, tag after green, and record the gate runs and the download checks in a follow-up.

### Out

- Feature work.

## Acceptance criteria

- The tag run is green, and its files and image are published. The downloaded MSI and `AppPortalSetup.exe` match their `SHA256SUMS` lines, and `ghcr.io/duresa7/app-portal-server:0.11.0` can be read without credentials.
- Roadmap rows M8-01 to M8-08 are **Done**.

## Touches

`README.md`, `docs/images/*`, `docs/ROADMAP.md`, `Directory.Build.props`.
