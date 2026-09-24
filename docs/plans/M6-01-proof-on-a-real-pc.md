# M6-01: Proof on a real PC

**Milestone:** 6 (0.9.0)
**Depends on:** M2-06, M3-07, M3-09, M3-11
**Unlocks:** M6-04

## Goal

A per-user install and an install that finishes at a restart have both run on a real Windows PC. One script proves it before every release and leaves a pass or fail summary that the release pull request carries. This closes the caveat that has stood since 0.5.0.

## Context

- **What has never run for real.** `WindowsUserSessions` (`src/AppPortal.Agent/Sessions/WindowsUserSessions.cs`) is the Win32 code that finds a person's session, takes their token and starts an installer in it. It has only run against `FakeSessions`. The restart path has not run on a PC either: an exit code of 3010 or `requiresReboot` leaves the install at "Restart to finish", then the heartbeat's boot time settles it through `RestartConfirmation.Apply` (`Agent/AgentEndpoints.cs`). The release gate's Windows job proves machine-wide installs only.
- **Why this is not a CI step.** A GitHub-hosted runner has nobody signed in, so every per-user install parks, and a job cannot survive a restart. So this is a script an operator runs by hand, elevated, on a disposable Windows 11 PC or VM where a test account is signed in. It resumes itself after the restart. It is the same kind of tool as `deploy/windows/ci-installer-test.ps1`, and it reuses that script's patterns and `Reset-AppPortal.ps1`.
- **How the script acts as the device.** The agent writes the server URL and device token to `%ProgramData%\AppPortal\client.json` when it enrolls, and `ci-installer-test.ps1` already reads the token from there, elevated. For an install, the client sends exactly one request: `POST /api/v1/installs` with the bearer token and `X-AppPortal-User: DOMAIN\user` (`src/AppPortal.Client/Services/PortalApiClient.cs`). The server trusts that header because the token is the credential (the roadmap's Identity decision). So the script sends the same request with the test account in the header. It does not drive the client window: the client has no switch that installs anything (only `--demo` and `--screenshot`), adding one would put test-only surface into a shipped binary, and clicking the window proves nothing more about the agent.
- **Who restarts the PC.** The agent never restarts a PC (M3-09). The client's Restart button runs `shutdown.exe /g /t 60 /c "..."` (`src/AppPortal.Client/ViewModels/AppItemViewModel.cs`). The script runs the same command.
- **Why the server runs on the test PC.** The script starts the server from the build output in fake Action1 mode, as `ci-installer-test.ps1` does, with a data directory that survives the restart; the resume step starts it again. Running it locally means the device's boot time and the server's "waiting since" come from one clock, no administrator credential crosses a network, and nothing is left behind on a real deployment.
- **Why the agent is set to Manual across the restart.** A local server comes up after the agent at boot. Once a device is enrolled, a failed heartbeat retries after the last interval the server gave, 900 seconds before any answer (`src/AppPortal.Agent/HeartbeatWorker.cs`), and the startup inventory sweep does not retry at all. The script therefore sets `AppPortalAgent` to Manual before the restart, starts it once the server answers, and then sets it back to Automatic. The agent's first start after the boot then meets a live server, which is what a fleet PC meets.
- **Test packages.** The script compiles a purpose-built installer from `deploy/windows/ProofInstaller.cs` with the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, C# 5 only), and serves the result over loopback HTTP, which `DirectPackageDefinition.ValidateUrl` accepts. The script works out the SHA-256 and size after the build. This beats a winget or third-party package: it needs no download and no outside publisher, an upstream version change cannot break it, no binary is committed to a public repository, it exits with whatever code it is told to, and it writes down who it ran as, whether it was elevated, and in which session, which are exactly the facts the proof needs.
- **Two defects in the session launcher.** Found by reading the code. The proof would hit the first one, so fix both in this package:
  1. **An install hangs if its installer leaves a child running.** `CreateProcessAsUser` passes inheritable handles, so an installer that starts its app when it finishes (every Squirrel installer does) passes the pipe's write end on to that app. After the installer exits, `await output` waits for the end of the stream. The read is a synchronous `FileStream`, so the deadline token cannot interrupt it, and `RunAsAsync` never returns. `JobRunner.ReportAsync` keeps renewing the lease, the install sits at Installing for as long as the app runs, and the agent runs one job at a time, so every other install on the device waits too.
     Fix: once the installer has exited, wait at most a few seconds for the transcript, then cancel the pending read (for example `CancelIoEx` on the read end) and return what was read.
  2. **A fast installer can lose its exit code.** `Process.GetProcessById` holds no handle, and the method closes the only handle it is sure of before `Process` has opened its own. An installer that exits in that window loses its exit code, and `JobRunner` reports "Executor failed (InvalidOperationException)".
     Fix: keep `information.hProcess` open until the exit code has been read from it.
- **Where the proof may find more.** Machine-wide inventory comes from winget run as SYSTEM; per-user inventory comes from that same `winget.exe`, found under `WindowsApps`, started in the person's session (`src/AppPortal.Agent/Jobs/SoftwareReporter.cs`). No gate has run either one; the gate's managed check reads PowerShell's module list. `RestartConfirmation` counts an empty list as found, so a restart install can turn Succeeded without anything having been checked, and the proof requires the app to be listed. If either list cannot be read on a real PC, do the smallest correct fix and say so in the pull request. If that fix would outgrow this package, raise it instead (rule 3).

## Scope

### In

- **`deploy/windows/ProofInstaller.cs`.** A console program that compiles with the in-box `csc.exe`.
  - `install --id <id> --scope user|machine [--exit <code>] [--linger <seconds>]`:
    - Copies itself to `<root>\App Portal Proof\<id>\proof-installer.exe`, where the root is `%LOCALAPPDATA%` for user scope and `%ProgramFiles%` for machine scope.
    - Writes `installed.json` beside the copy, with the account (`WindowsIdentity.GetCurrent().Name`), whether the token is elevated, the session id, `%LOCALAPPDATA%` and the UTC time.
    - Writes the uninstall entry `AppPortalProof-<id>` under HKCU or HKLM (64-bit view), with `DisplayName` "App Portal Proof <id>", a version, a publisher and a `QuietUninstallString` that runs the copy with `uninstall`.
    - Prints one line naming what it did and as whom.
    - With `--linger`, starts the copy as a child that inherits handles and sleeps, then exits without waiting for it.
    - Exits with `--exit`, 0 by default.
  - `uninstall` removes the entry and `installed.json`.
- **`deploy/windows/Test-RealPc.ps1`.** PowerShell 7, run elevated.
  - **Start phase** (the operator runs it while the test account is signed in):
    1. Preflight: refuse without `-Disposable`; refuse under `GITHUB_ACTIONS`; require the account to be signed in (`explorer.exe` running as that account is the test); require the account not to be a local administrator.
    2. Run `Reset-AppPortal.ps1` and clear the leftovers of an earlier proof.
    3. Build the proof installer and start the loopback package host.
    4. Start the server with a proof administrator.
    5. Import the proof catalog and create an `--engine agent` enrollment key.
    6. Install the MSI with `SERVERURL` and `ENROLLMENTKEY`, then wait for enrollment.
    7. Run the per-user checks, then the restart installs and the restart-pending checks (see Steps).
    8. Register the resume task, write the state file, set the agent to Manual and schedule the restart.
  - **Resume phase** (`-Resume`, run by the scheduled task as SYSTEM at startup):
    1. Remove the task first, so a crash cannot loop at every boot.
    2. Start the package host and the server on the same data directory.
    3. Request the parked per-user install while the agent is still stopped.
    4. Start the agent, then restore Automatic.
    5. Run the restart-confirmed and parked checks.
    6. Write the summary and clean up.
  - **`-Result`** (the operator runs it elevated after signing in) waits for the resume phase to finish and prints the summary. It exits 0 on PASS, 1 on FAIL and 2 while still running.
  - **`-Cleanup`** removes everything the proof left: the task, the processes, the proof files and uninstall entries, the state directory apart from the summary and logs, and App Portal itself through `Reset-AppPortal.ps1`.
  - After a PASS the resume phase cleans up on its own. After a FAIL it leaves App Portal installed for inspection and names `-Cleanup`. The server and package host are stopped either way.
- **`deploy/windows/InstallerTestHelpers.psm1`.** `Write-Step`, `Wait-For` and `Invoke-Msi` move here out of `ci-installer-test.ps1`. Both scripts import it, and the gate script's behaviour does not change.
- **Session launcher fixes.** The two defects in Context. The rule for how long to wait for the transcript lives in a small helper with no Win32 in it, so it can be unit-tested. The interop stays in `WindowsUserSessions.cs`.
- **Windows gate job.** One step that parses `Test-RealPc.ps1` and compiles `ProofInstaller.cs`. The script only runs by hand, and a hand-run script rots unseen otherwise.
- **Roadmap.** The M6-01 row. After a passing run, rewrite the caveat paragraph to say what was proven and on what (Windows 11, a standard account, the MSI from the release commit). Say nothing about the machine it ran on.

### Out

- **Clicking the client's Install and Restart buttons.** The script sends the same request and runs the same command, and the client's view models have their own tests.
- **A winget or Store per-user package.** It needs the network, its versions drift, and it cannot report who it ran as. The per-user inventory sweep already runs winget in the session.
- **A remote or production server, and the Docker image.**
- **Automatic sign-in after the restart.** It stores a password in the registry. The operator signs in by hand.
- **Windows 10**, which is out of support.
- **The M3-07 two-account matrix beyond a header check.**
- **Installing for every account.**
- **Fixing the heartbeat retry interval, or the same inherited-pipe shape in `ProcessRunner`.** Both go in as issues.

## Interface

**Operator flow.** This is also the script's comment-based help, which is the runbook.

1. Prepare a disposable Windows 11 PC or VM with PowerShell 7; the .NET 10 SDK, or the ASP.NET Core 10 runtime plus `-ServerDll`; a checkout of the release commit; and the `AppPortal-msi` artifact from the full gate run on that commit.
2. Create a standard local account, sign in to it, and sign every other account out. Windows refuses the client's restart command while another account is signed in (`shutdown.exe` exit code 1191), which the first run for 0.9.0 found; the script refuses to start then. Give the account a password, and turn off "Use my sign-in info to automatically finish setting up after an update" in its Sign-in options: with either one missing, Windows signs the account straight back in after the restart, so the parked-install check would never see nobody signed in. The fifth run for 0.9.0 found that an empty password does this even with the switch off. The script refuses to start without both.
3. In the test account's session, open PowerShell 7 as administrator through UAC with administrator credentials.
4. Run `./deploy/windows/Test-RealPc.ps1 -Msi <path> -Account <user or COMPUTER\user> -Disposable [-ServerDll <path>] [-Port 5090]`.
5. The PC restarts after 60 seconds. Wait at the sign-in screen for two minutes, then sign in as the test account.
6. Run `./deploy/windows/Test-RealPc.ps1 -Result` elevated, and attach the summary to the release pull request.

**Credentials.** These are never written to a file, a log, a transcript or a command line.

- The server administrator password comes from `APPPORTAL_ADMIN_PASSWORD` if the operator has set it. Otherwise the script asks with `Read-Host -AsSecureString`.
  - It sits in the process environment only for the `admin add` call, and is removed before the server starts, so the server does not inherit it.
  - It is used once more, to take an admin API token for the restart-pending check. That token is revoked at the end of the start phase.
  - The operator can use the same password at `http://127.0.0.1:<port>/admin` during the run.
- The device token is read from `client.json` into a variable and never printed.
- No Windows password is asked for or stored.
- The resume phase needs no credential.

**State.** Everything lives in `%ProgramData%\AppPortalProof\`, restricted to SYSTEM and Administrators. It holds `state.json` (paths, port, account, install ids, the `pwsh` and `dotnet` paths, the start time; no secrets), `server-data\`, `packages\`, `logs\` and the summary. It is never under `%ProgramData%\AppPortal`, which `Reset-AppPortal.ps1` deletes. The resume task is named `AppPortalProofResume`, runs as SYSTEM at startup, and has a two-hour limit.

**Proof catalog.** Every app has `userRemovable: true` and a `match.nameEquals` of its uninstall `DisplayName`.

| id | scope | installer | restart |
|---|---|---|---|
| `proof-user` | `user` | `install --id proof-user --scope user --linger 300` | none |
| `proof-restart-code` | `machine` | `install --id proof-restart-code --scope machine --exit 3010` | from the exit code |
| `proof-restart-flag` | `machine` | `install --id proof-restart-flag --scope machine` | `requiresReboot: true` |

**Summary.** The file is `%ProgramData%\AppPortalProof\real-pc-proof.md`, in this shape:

```
# App Portal real-PC proof: PASS

- Agent build: 0.9.0+<commit>        (ProductVersion of the installed AppPortal.Agent.exe)
- MSI SHA-256: <hash>
- Windows: <caption> (build <n>)
- Test account: standard user
- Started / Finished: <UTC> / <UTC>

| Check | Result | Detail |
|---|---|---|
| per-user-session | PASS | ... |
```

- Checks, in order: `per-user-session`, `per-user-profile`, `per-user-reported`, `per-user-transcript`, `per-user-removal`, `restart-pending`, `restart-confirmed`, `parked-until-sign-in`.
- Each result is PASS, FAIL or NOT RUN. The heading reads PASS only when all eight pass.
- Details replace the computer name with `<pc>` and the account with `<account>`. The summary never contains a computer name, account name, URL, token or password.

**Release contract.** M6-04 does not tag until a `real-pc-proof.md` with a PASS heading is attached to the release pull request, and its Agent build is either the release version with the release commit, or a commit whose diff to the release commit touches nothing under `src/`. If the build does not stamp the commit into ProductVersion, the script records `git rev-parse HEAD` from the checkout instead, and the summary says which it used.

## Steps

1. **Session launcher.** Fix both defects. Extract the transcript-grace rule and test it: an installer that exits while its child holds the pipe returns within the grace period, with the output so far. `FakeSessions` tests stay green.
2. **`ProofInstaller.cs`.** Compile it with the in-box `csc.exe` and try each mode by hand once.
3. **Helpers.** Extract them into `InstallerTestHelpers.psm1`, and point `ci-installer-test.ps1` at the module.
4. **Start phase of `Test-RealPc.ps1`**, in this order:
   1. **Per-user install.** Request `proof-user` with `X-AppPortal-User` set to the account and wait for Succeeded. Then check:
      - `per-user-session`: `installed.json` names the account, is not elevated, and carries the account's session id.
      - `per-user-profile`: the files are under the account's own profile (found through its SID and `Win32_UserProfile`), and the entry is under `HKU\<SID>`. Nothing is in the SYSTEM profile or HKLM.
      - `per-user-reported`: `requestedBy` is the account. `GET /api/v1/device/installed` lists `proof-user` for the account, and does not list it without the header or with another account's header.
      - `per-user-transcript`: the install reached Succeeded while the lingering child was still running, and a job log under `%ProgramData%\AppPortal\jobs` holds the installer's line. Then stop the child.
   2. **Per-user removal.** Request it as the account. `per-user-removal`: `installed.json` and the entry are gone, and the account's Installed list no longer shows the app.
   3. **Restart installs.** Request both restart apps. `restart-pending`: each is Running with `rebootState` `pending` and detail "Restart to finish", and the admin installs list filtered to restarts lists both. Take the filter's parameter name from `docs/api.md`.
   4. **Hand over to the resume phase.** Write the state, register the task, set the agent to Manual, and run the client's `shutdown.exe` command.
5. **Resume phase:**
   1. Before starting the agent, note whether the account has a session. Request `proof-user` as the account.
   2. Start the agent. `restart-confirmed`: both restart installs reach Succeeded with `rebootState` `confirmed` and detail "Installed. The PC has restarted.". Each app has exactly one install row, its `installed.json` names `NT AUTHORITY\SYSTEM`, and the machine-wide Installed list shows it.
   3. `parked-until-sign-in`: the per-user install showed "Waiting for <account> to sign in.", then succeeded into the profile after sign-in, within 30 minutes. If the account was already signed in at step 1, this check is NOT RUN, with the reason "signed in too early; wait at the sign-in screen".
   4. Write the summary. Clean up on PASS.
6. **`-Result` and `-Cleanup`.**
7. **Gate step** in the Windows job.
8. **Run it** on a disposable Windows 11 PC or VM with the MSI the full gate built from this branch. Fix what it finds, attach the passing summary to the pull request, and update the roadmap.

## Acceptance criteria

- One elevated run on a disposable Windows 11 PC or VM, with a standard account signed in, ends in a PASS summary with all eight checks passed. The operator does nothing beyond the start command, one sign-in and `-Result`.
- A per-user install lands in the requesting account's profile and hive, runs unelevated in that account's session, and shows in that account's Installed list only.
- A per-user installer that leaves its app running still completes. Before the fix, `per-user-transcript` fails; after it, the check passes.
- Both restart installs wait at "Restart to finish" until the restart and then succeed on their own, with no second row. They count as confirmed only if the software is listed, never because nothing was reported.
- A per-user install asked for while nobody is signed in parks, and runs when the account signs in.
- Preflight refuses, with a readable reason, when `-Disposable` is missing, the account is not signed in, the account is an administrator, or the script is running in GitHub Actions.
- Searching the summary for the computer name or the account name finds nothing. The admin password appears in no file, transcript or child process environment, and the device token in no output.
- After the resume phase, whatever the result, `AppPortalProofResume` does not exist and `AppPortalAgent` starts automatically again.
- `dotnet test` is green. The Windows gate parses the script and compiles the installer. `ci-installer-test.ps1` passes unchanged in behaviour.

## Verification

- `dotnet test`.
- The full gate from the Actions tab on the branch, with the Windows jobs ticked.
- The proof run described above, with its summary in the pull request.
- One run with the account signed out, to show the preflight refusal.
- One run of the transcript check with the launcher fix reverted, to show that check failing.

## Risks

- **The two launcher defects come from reading the code.** The pipe hang is close to certain for any installer that starts its app when it finishes. The lost exit code is a race that a tiny installer may never hit. The proof run confirms both.
- **Inventory may be the weak point.** No gate has run winget as SYSTEM, or the SYSTEM-located `winget.exe` in a standard user's session. If either fails, the fix may be bigger than this package, for example reading the person's uninstall hive through `WindowsUninstallRegistry` instead of winget. Decide then whether to widen this package or split the work out.
- **Two design choices depart from a real fleet.** Setting the agent to Manual across the restart is a small deviation; the alternative is a remote server, which brings credentials and clock skew. The parked check needs the operator to wait at the sign-in screen.
- **Installs waiting for a restart count as Running**, so they count against the limit of 3 active installs per device. The proof stays within it (2 waiting plus 1).
- **Issues to file, not fix here:** after a failed heartbeat an enrolled PC retries after the last interval (900 s at first), so a PC whose network comes up late waits up to 15 minutes to settle a restart; and `ProcessRunner` has the same inherited-pipe shape for machine installs, where the 60-minute timeout bounds it and the result is a false failure.

## Touches

`deploy/windows/Test-RealPc.ps1` (new), `deploy/windows/ProofInstaller.cs` (new), `deploy/windows/InstallerTestHelpers.psm1` (new), `deploy/windows/ci-installer-test.ps1`, `src/AppPortal.Agent/Sessions/WindowsUserSessions.cs`, `src/AppPortal.Agent/Sessions/SessionTranscript.cs` (new), `tests/AppPortal.Agent.Tests/SessionTranscriptTests.cs` (new), `.github/workflows/ci.yml`, `docs/ROADMAP.md`.
