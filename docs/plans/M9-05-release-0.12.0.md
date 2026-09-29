# M9-05: Release 0.12.0

**Milestone:** 9 (0.12.0)
**Depends on:** M9-01, M9-02, M9-03, M9-04

## Goal

Ship milestone 9 through the gate: engines as a list, the Intune engine, sign-in with Entra ID or any OpenID Connect provider, and device groups with per-group catalogs. Then review the code of milestones 8 and 9 as a whole.

## Scope

### In

- **Code review.** Review every change since v0.10.0 against the repository's standards (`AGENTS.md`, `docs/plans/README.md`) and against each package's plan, and fix what the review finds in its own pull requests before the tag. Record the review's result in this pull request.
- **README.** The feature list names Intune, OpenID Connect sign-in and device groups.
- **Roadmap.**
  - Rows M9-01 to M9-05 **Done**.
  - A "Milestone 9 shipped as v0.12.0" paragraph that says what was proven against a real Intune tenant and a real identity provider, and what only against fakes. The release is not signed.
  - Review the Deferred list.
- **Version.** Bump `Directory.Build.props` to 0.12.0. Run the full gate on `main` with the Windows jobs, tag after green, and record the gate runs and the download checks in a follow-up.

### Out

- Feature work other than the review's fixes.

## Acceptance criteria

- The review is recorded, and every finding it confirms is fixed or recorded as an issue.
- The tag run is green, and its files and image are published. The downloaded MSI and `AppPortalSetup.exe` match their `SHA256SUMS` lines, and `ghcr.io/duresa7/app-portal-server:0.12.0` can be read without credentials.
- Roadmap rows M9-01 to M9-05 are **Done**.

## Touches

`README.md`, `docs/ROADMAP.md`, `Directory.Build.props`, and the files each review fix names.
