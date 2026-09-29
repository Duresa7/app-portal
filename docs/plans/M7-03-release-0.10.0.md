# M7-03: Release 0.10.0

**Milestone:** 7 (0.10.0)
**Depends on:** M7-01, M7-02

## Goal

Ship milestone 7 through the gate: the client's admin flows proven on a PC, and a per-user installed list that is swept each time the person signs in.

## Scope

### In

- **The sign-in sweep.** The release does not wait for a run on a PC. M7-02 adds only the decision of when to sweep, and its tests cover that decision. The two Windows calls it makes were proven on a real PC by the 0.9.0 proof: `SignedInAccounts()` started the install that was parked until the account signed in, and `SoftwareReporter` reported the per-user install from inside that account's session. If a PC with a person signed in is at hand, install the MSI from the release commit, sign in, and look for "Reporting what {Account} has installed, because they signed in" in `%ProgramData%\AppPortal\agent.log` within two minutes. Record the result in this pull request.
- **README.** Check that the text on the installed list and on the admin area of the client is still true.
- **Roadmap.**
  - Rows M7-01 to M7-03 **Done**.
  - A "Milestone 7 shipped as v0.10.0" paragraph that says what was proven and on which Windows edition, and names no machine. The release is not signed; say so.
  - Mark the 0.8.0 caveat closed by M7-01, and the per-user freshness note in the milestone 3 paragraph closed by M7-02.
  - Review the Deferred list and write under Next what is left, or that nothing is planned.
- **Version.** Bump `Directory.Build.props` to 0.10.0. Run the full gate on `main` with the Windows jobs ticked, and tag after green.

### Out

- Feature work.
- Signing. The owner decided on 2026-09-28 not to do it in this milestone, so the switch from M6-03 stays off.

## Acceptance criteria

- The tag run is green, and its files and image are published. The downloaded MSI and `AppPortalSetup.exe` match their `SHA256SUMS` lines, and `ghcr.io/duresa7/app-portal-server:0.10.0` can be read without credentials.
- Roadmap rows M7-01 to M7-03 are **Done**, and the Deferred list is reviewed.

## Touches

`README.md`, `docs/ROADMAP.md`, `Directory.Build.props`.
