# M7-02: A fresh per-user list when a person signs in

**Milestone:** 7 (0.10.0)
**Depends on:** M3-07, M5-04
**Unlocks:** M7-03

## Goal

The agent reports what a person's own profile carries each time that person signs in, not only after an install for them. A per-user installed list then describes the PC as it was at the person's last sign-in, and after a restart it is new again as soon as they are back.

## Context

- **What is swept today.** `StartupSoftwareSweep` (`src/AppPortal.Agent/Jobs/StartupSoftwareSweep.cs`) reports the machine-wide list at every service start, and so after every restart and every upgrade. A person's own list is reported only by `JobRunner` straight after an install for that person. `SoftwareReporter.ReportAsync(settings, ct, account)` and `SoftwareReporter.ReportManagersAsync(settings, ct, account)` run winget and the per-user package managers inside that person's session, through `IUserSessionLauncher.RunAsAsync`, because software in a profile is invisible from outside it.
- **Why that is not enough.** The roadmap's milestone 3 paragraph says so: "a per-user list stays as fresh as that person's last install". Software that the person removes, or adds themselves, stays wrong in the portal until the portal installs something else for them.
- **How the agent sees a sign-in.** `IUserSessionLauncher.SignedInAccounts()` lists every account with an interactive session. `JobRunner` already sends that list on every poll in the `X-AppPortal-Sessions` header, so that parked per-user installs start when the person arrives. The sweep uses the same list. It needs no service control events, and `FakeSessions` tests it on either operating system.
- **Why it waits.** At the first sign-in to a profile, Windows registers the App Installer package, and with it the `winget.exe` alias, some time after the desktop appears. A list read too early fails, and `SoftwareReporter` then keeps the last report, which is safe but useless. So the sweep waits until an account has been signed in for a settle time.
- **The server does not change.** `POST /api/v1/agent/software?account=...` already replaces one person's list per source. `RestartConfirmation` (`src/AppPortal.Server/Installs/RestartConfirmation.cs`) still settles a per-user restart install at the first heartbeat after the boot, which usually comes before the person signs in. Making it wait for their fresh list is a separate change, and this package does not make it. Only its comment, which says how fresh a person's list is, changes.

## Scope

### In

- **`src/AppPortal.Agent/Jobs/SignInSoftwareSweep.cs`**, a `BackgroundService`:
  - Polls `SignedInAccounts()` every 30 seconds.
  - Keeps, for each account signed in now, the time it was first seen in this sign-in and whether it has been swept.
  - When an account has been seen for 60 seconds and is not swept yet, and `PortalSettings` says the PC is enrolled, it runs `ReportAsync(settings, ct, account)` and then `ReportManagersAsync(settings, ct, account)`, and marks the account swept.
  - An account that is gone at a poll is forgotten, so its next sign-in is swept again.
  - At service start it knows nobody, so everyone signed in at that time is swept once. That covers a restart of the service while people are signed in, which an upgrade causes.
  - Accounts are compared without regard to case, as Windows compares them.
  - One sweep at a time, in the order the accounts arrived. A failed sweep is not repeated until the next sign-in; `SoftwareReporter` already logs it and keeps the last report.
  - The poll is `internal Task<IReadOnlyList<string>> PollAsync(DateTimeOffset now, CancellationToken ct)`, which returns the accounts it swept. `ExecuteAsync` calls it in a loop with the real time. Tests call it with the times they need.
- **`src/AppPortal.Agent/AgentRun.cs`**: register the sweep beside `StartupSoftwareSweep`, inside `if (!once)`.
- **`src/AppPortal.Server/Installs/RestartConfirmation.cs`**: the comment says that a person's list is as fresh as their last sign-in or install. No change in behaviour.
- **`docs/ROADMAP.md`**: the milestone 3 paragraph says the per-user list is now swept at each sign-in, from 0.10.0.
- **README**, where it says when the installed list is updated, if it says so: add the sign-in.

### Out

- Making `RestartConfirmation` wait for a person's list after the restart.
- Sweeping on a schedule while a person stays signed in.
- Service session-change notifications.

## Interface

```csharp
public sealed class SignInSoftwareSweep(
    SoftwareReporter software,
    IUserSessionLauncher sessions,
    ILogger<SignInSoftwareSweep> logger,
    Func<PortalSettings>? loadSettings = null,
    TimeSpan? pollInterval = null,   // 30 seconds
    TimeSpan? settleTime = null)     // 60 seconds
    : BackgroundService
```

Log lines, one each: "Reporting what {Account} has installed, because they signed in", and nothing for a poll that finds nothing new.

## Steps

1. Tests first, in `tests/AppPortal.Agent.Tests/SignInSoftwareSweepTests.cs`, with `FakeSessions` and the fake process runner and HTTP handler that `SoftwareReporterTests` uses:
   - An account seen for less than the settle time is not swept.
   - An account seen for the settle time is swept once: winget list and each per-user manager run in its session, and the software is posted with `account=`.
   - The same account at later polls is not swept again.
   - An account that signs out and back in is swept again after the settle time.
   - An account signed in when the service starts is swept.
   - Two accounts are swept in the order they arrived.
   - A PC that is not enrolled sweeps nobody, and is swept once it is enrolled.
   - `DOMAIN\Person` and `domain\person` are one account.
2. The class, then the registration.
3. The comment and the documents.

## Acceptance criteria

- The tests above pass on Linux and on the Windows leg.
- The agent runs one sign-in sweep per sign-in, 60 seconds or more after it, and none while the person stays signed in.
- A PC that is not enrolled reports nothing.

## Verification

`dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release`. The release gate's Windows job has nobody signed in, so it cannot run this path. It does not need to: the two Windows calls the sweep makes, `SignedInAccounts()` and a per-user `ReportAsync`, ran on a real PC in the 0.9.0 proof, and this package adds only the decision of when to make them.

## Touches

`src/AppPortal.Agent/Jobs/SignInSoftwareSweep.cs` (new), `src/AppPortal.Agent/AgentRun.cs`, `src/AppPortal.Server/Installs/RestartConfirmation.cs`, `tests/AppPortal.Agent.Tests/SignInSoftwareSweepTests.cs` (new), `README.md`, `docs/ROADMAP.md`.
