# M6-04: Release 0.9.0

**Milestone:** 6 (0.9.0)
**Depends on:** M6-01, M6-02, M6-03

## Goal

Ship milestone 6 through the gate: the per-user and restart installs proven on a real PC, approved requests that point to an app, and releases that can be signed.

## Scope

### In

- **The real-PC proof.** Run `deploy/windows/Test-RealPc.ps1` on a disposable Windows 11 PC or VM against the MSI the full gate built from the release commit, and attach `real-pc-proof.md` to this pull request. The tag waits for a PASS heading whose Agent build satisfies the release contract in M6-01.
- **Signing.** If the owner's SignPath Foundation application has been approved and the M6-03 checklist is done, run the signed rehearsal on `main` first, and the tag is release-signed. If it has not, 0.9.0 ships unsigned with the switch in place, the release notes say so, and the first signed release is the next one. The release does not wait on an outside approval.
- **README.** Screenshots of the request link: the end-user Requests page with a linked request, and the admin Requests page with the decision dialog open for an approval, in light and dark. Check that "Checking a download" and "Code signing policy" (M6-03) read correctly for whichever signing state ships.
- **Roadmap.**
  - Rows M6-01 through M6-04 Done.
  - A "Milestone 6 shipped as v0.9.0" paragraph that says what the real-PC proof covered (Windows 11, a standard account, the MSI from the release commit) and nothing about the machine it ran on, and whether the release is signed.
  - Remove the per-user and restart caveat from the milestone 3 and 5 paragraphs, or mark it closed by this release.
  - Review the Deferred list and draft the next milestone under Next.
- **Version.** Bump `Directory.Build.props` to 0.9.0. Run the full gate on `main` with the Windows jobs ticked, and tag after green.

### Out

- Feature work.

## Acceptance criteria

- `real-pc-proof.md` with a PASS heading is attached to this pull request.
- Tag run green, assets and image published; the downloaded MSI and `AppPortalSetup.exe` match their `SHA256SUMS` lines, and `ghcr.io/duresa7/app-portal-server:0.9.0` is readable without credentials.
- When signing is on: `Get-AuthenticodeSignature` reports `Valid` and signer `CN=SignPath Foundation` for the downloaded MSI and `AppPortalSetup.exe`.
- Roadmap rows M6-01 through M6-04 Done; the Deferred list reviewed and the next milestone drafted.

## Touches

`README.md`, `docs/images/*`, `docs/ROADMAP.md`, `Directory.Build.props`.
