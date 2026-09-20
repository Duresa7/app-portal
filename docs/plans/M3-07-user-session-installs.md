# M3-07: Installs that run as the signed-in person

**Milestone:** 3 (0.5.0)
**Depends on:** M3-03, M3-04
**Unlocks:** M3-06, M3-11

## Goal

An agent package marked `scope: "user"` runs in the session of the person who asked for it, so software that installs into a user profile lands in the right one.

## Context

- The agent is a service running as SYSTEM (M2-02). Everything it starts inherits that account. A per-user installer started this way writes to `C:\Windows\System32\config\systemprofile\AppData\Local` and the person who asked sees nothing.
- This is the normal shape, not an edge case. Squirrel installers and most chat, music and streaming clients are per-user. winget answers `-1978335189`, no applicable installer, when such a package is asked for with `--scope machine`.
- `WingetPackageDefinition.Scope` already exists and validates `machine` or `user` (M3-01). `DirectPackageDefinition.Scope` arrives with the same package. Nothing reads either yet.
- An install carries `requested_by` as `DOMAIN\user` (M1-02). For a machine-wide install that is a record. For a per-user install it decides which profile the software goes into, so it becomes load-bearing.

## Scope

### In
- `IUserSessionLauncher` in the agent: `Task<ProcessResult> StartAsync(string account, string file, string arguments, IProgress<...> p, CancellationToken ct)`. The Windows implementation finds the session whose owner matches `account` through `WTSEnumerateSessions` and `WTSQuerySessionInformation`, takes the token with `WTSQueryUserToken`, duplicates it with `DuplicateTokenEx`, builds the block with `CreateEnvironmentBlock`, and starts the process with `CreateProcessAsUser` on `winsta0\default`. It does not elevate: a per-user installer must not need it, and a per-user installer that asks for elevation is a packaging error the detail should name.
- Job state `waiting_for_user`, added to the set in M3-02. A user-scope job whose requester is not signed in parks in this state instead of failing, and the agent starts it when that account next signs in. A job parked longer than 7 days fails with "Nobody signed in as DOMAIN\user within 7 days".
- Executors honour the scope. winget passes `--scope user`. A direct `exe` or `msi` runs through the launcher. A direct `msix` uses `Add-AppxPackage` in the session for user scope and keeps `Add-AppxProvisionedPackage` for machine scope; `silentArgs` is not used for either.
- Download stays with the service. The agent downloads to `%ProgramData%\AppPortal\downloads` as SYSTEM and grants the target account read access on the verified file, so a large download is not repeated per person and an unprivileged session never writes into the cache.
- Per-user inventory: `device_software` gains `account TEXT` (NULL for machine-wide) through migration 011. `GET /api/v1/device/installed` returns machine-wide software plus the calling requester's own, never another person's.
- Client shows "Installs for you" under the button on a user-scope app, so the difference is visible before the install rather than discovered after it.

### Out
- Installing for every person on the device at once. Running anything as a person who is not signed in. Elevation inside a user session.

## Interface

`IUserSessionLauncher` above. Job state `waiting_for_user`. `device_software.account`. Install detail strings: "Waiting for DOMAIN\user to sign in", "Installing for DOMAIN\user".

## Steps

1. `IUserSessionLauncher` with a fake, and the job path through it under test; every state transition covered without Win32.
2. The Windows implementation behind `OperatingSystem.IsWindows()`, with the interop in one file and no other file referencing `advapi32` or `wtsapi32`.
3. Scope handling in both executors; the msix split.
4. Migration, inventory filter, contract and client text.
5. VM test: a per-user installer with one person signed in, with nobody signed in, and with a different person signed in.

## Acceptance criteria

- A user-scope app asked for by `CONTOSO\ada` while `CONTOSO\ada` is signed in appears in her profile and in her Installed list, and not in another person's.
- The same app asked for while nobody is signed in parks, and installs when she next signs in, with the client showing why it waits.
- A machine-wide install is unchanged and still runs as SYSTEM.

## Verification

`dotnet test`; VM matrix on Windows 10 22H2 and Windows 11, each with two local accounts.

## Touches

`src/AppPortal.Agent/Sessions/**` (new), `src/AppPortal.Agent/Executors/*`, `src/AppPortal.Agent/Jobs/*`, `src/AppPortal.Server/Agent/AgentJobStore.cs`, `Api/PortalEndpoints.cs`, `Data/Migrations/011-software-account.sql`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`, `Views/*.axaml`, `tests/AppPortal.Agent.Tests/UserSessionTests.cs`, `tests/AppPortal.Server.Tests/InstalledSoftwareTests.cs`.
