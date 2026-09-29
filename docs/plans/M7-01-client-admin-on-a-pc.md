# M7-01: The client's admin flows, clicked through on a PC

**Milestone:** 7 (0.10.0)
**Depends on:** M4-05
**Unlocks:** M7-03

## Goal

Two admin flows in the Windows client, proven so far only at the API, are done by hand in the real client on a real Windows PC: a session that the web admin ends signs the client out, and an enrollment key made in the client enrolls a PC. The result is recorded, and the caveat that has stood since 0.8.0 is closed.

## Context

- The 0.8.0 paragraph in the roadmap says both flows "have been proven at the API but not yet clicked through on a PC". Tests cover the view models over a fake client (`tests/AppPortal.Client.Tests`), and the release gate draws the admin dashboard in demo mode. No run has used the real window against a real server.
- **How the client ends a session.** `AdminSession` (`src/AppPortal.Client/Services/AdminSession.cs`) subscribes to `IAdminApiClient.Unauthorized`. The first call that the server refuses clears the token, deletes `%LOCALAPPDATA%\AppPortal\admin-session.bin`, and raises `Ended` with `AdminApiClient.SessionEndedMessage`: "Your administrator session has ended. Sign in again to continue." `MainViewModel.OnAdminSessionEnded` shows that sentence as `AdminNotice`.
- **How the web admin ends a session.** The web Admins page (`src/AppPortal.Server/Pages/Admin/Admins.cshtml.cs`) calls `AdminSessionStore.RevokeAllFor` when it disables an account or resets its password. That ends every session the account holds, bearer sessions of the client included. The web page refuses to disable the account that is signed in to it, so the proof uses two accounts: one for the web, one for the client.
- **How a key reaches a PC.** The client's Enrollment keys page shows a new key once, with a copy button (M4-05). On a fleet PC the key goes into `AppPortalSetup.exe` or the MSI's `ENROLLMENTKEY`, and the MSI writes it into `enroll.json` beside `client.json`. The agent's `EnrollmentService` (`src/AppPortal.Agent/Enrollment/EnrollmentService.cs`) sends it to `POST /api/v1/enroll` and writes the device token to `client.json`. `installer-verify` proves the wizard, the MSI and the enrollment on every release, with a key that the server CLI makes. The part that no run has proven is that the key the client shows is a key that works.
- **How the proof stays off the PC.** `APPPORTAL_CONFIG` (`src/AppPortal.Shared/PortalSettings.cs`) moves the file that the client and the agent read, and the agent keeps its state in the same folder. So the client and the agent run from the build output against a temporary folder, and the server runs from the build output in fake Action1 mode. Nothing is installed, no service is registered, and the PC's own `%ProgramData%\AppPortal` is not touched. The one thing the proof leaves on the server is the device record, and the server is thrown away at the end.

## Scope

### In

- **The proof**, done once by hand on Windows 11 with the Release build of `main`:
  1. Start the server from `src/AppPortal.Server/bin/Release/net10.0` on `http://127.0.0.1:5080`, with `Action1__Mode=Fake` and `Portal__DataDirectory` in a temporary folder.
  2. Add two administrators with `admin add` and `APPPORTAL_ADMIN_PASSWORD`: `proof-web` for the web and `proof-client` for the client. Make each password new for this run, keep it in a file in the temporary folder, and put it on the clipboard to paste it. Never type it into a command line, a log or the pull request.
  3. Write `client.json` with only `serverUrl` into a second temporary folder. Start `AppPortal.exe` with `APPPORTAL_CONFIG` set to that file.
  4. In the client: **Admin**, sign in as `proof-client`. Open **Enrollment keys**, make a key named "M7-01 proof" for the agent engine, and copy it with the copy button in the show-once dialog. Close the dialog.
  5. Write the clipboard into `enroll.json` beside `client.json` as `{"serverUrl": ..., "enrollmentKey": ...}`, from the clipboard straight to the file, so that the key is never printed. Run `AppPortal.Agent.exe --console --once` with the same `APPPORTAL_CONFIG`. It must exit 0, and `client.json` must then carry a device token.
  6. In the client: **Devices**, refresh. The PC is listed, enrolled with "M7-01 proof", with a heartbeat from the last minute. **Enrollment keys** shows one use on "M7-01 proof".
  7. In a browser: sign in to `/admin` as `proof-web`. On **Admins**, disable `proof-client`.
  8. In the client: open any admin page, or press Refresh. The client goes back to the admin sign-in form and shows "Your administrator session has ended. Sign in again to continue." `admin-session.bin` is gone.
  9. Stop the server, the client and the agent. Delete both temporary folders.
- **Record.** The pull request carries what each step showed, with the build commit, the Windows edition and version, and screenshots of steps 4, 6 and 8 with the key value hidden. Name no machine.
- **A defect the proof finds** is fixed in this package if the fix is small, and the pull request says what it was. If the fix is larger, open an issue and record the step as failed.

### Out

- The wizard and the MSI. `installer-verify` proves them on every release.
- Driving the client from a script or from CI. The client has no switch that signs in or clicks, and adding one would put test-only surface into a binary that ships.
- A key revoked in the client, and a device token rotated in the client. They are the same API calls that are proven today, and the roadmap does not list them.

## Acceptance criteria

- A key made and copied in the client enrolled a PC: the agent exited 0 with a device token, and the client's Devices page listed the PC.
- Disabling the client's account on the web signed the client out on its next call, with the sentence above, and the stored session file was deleted.
- The pull request records both, and the roadmap's 0.8.0 paragraph marks the caveat closed.

## Verification

The proof above. `dotnet test` must still pass if the proof changed code.

## Touches

`docs/ROADMAP.md`. Code only if the proof finds a defect, plus its tests.
