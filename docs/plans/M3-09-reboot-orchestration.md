# M3-09: Restarts as part of the install

**Milestone:** 3 (0.5.0)
**Depends on:** M3-04, M3-02
**Unlocks:** M3-06

## Goal

An install that is not finished until the PC restarts says so, asks for the restart, and confirms itself afterwards, instead of reporting success for software that does not yet work.

## Context

- M3-04 maps exit code 3010 to succeeded with "Restart required" in the detail, and stops there. Nothing asks for the restart and nothing checks afterwards. The person reads a green row and finds out the truth when the software will not start.
- A kernel-mode driver is the clear case: the service is registered by the installer but is not loaded until boot. `requiresReboot` on the definition arrives with M3-01 for exactly this, and covers the installers that need a restart without saying so in an exit code.
- Nobody may restart somebody else's work PC without asking. The portal asks; the person decides when.

## Scope

### In
- Migration 013: `installs` gains `reboot_state TEXT` with values NULL, `pending`, `confirmed`. The install state column keeps its existing values, so history and the existing pages keep working.
- An install ends in `reboot_state = 'pending'` when the runner exits 3010 or 1641, or when the definition says `requiresReboot`. The install state becomes `succeeded` only after confirmation; until then it is `running` with the detail "Restart to finish".
- Confirmation: the agent records the boot time it last reported. After a boot later than the install, it re-runs the same check the executor uses to report installed software, and posts the result. Present means confirmed and the install succeeds; absent means the install fails with "The software was not there after the restart".
- A confirmation that never arrives because the device is never restarted is not a failure. The install sits at "Restart to finish" until the device restarts, and the admin list can filter for it.
- Client: a banner on the card and in Activity, "Restart this PC to finish installing", with a Restart button that asks for confirmation first and a Later button. Never an automatic restart, and never a countdown the person did not agree to.
- Admin: the installs list shows "Restart pending" as a distinct state with its own filter, and the dashboard counts them.

### Out
- Forcing or scheduling a restart. Maintenance windows. Restarting to finish an Action1 install; Action1 runs its own.

## Interface

`installs.reboot_state`. Route `POST /api/v1/agent/jobs/{id}/confirm`, body `{ok, detail}`, device bearer auth. `InstallRequest.RebootState: string?` in the contract. Detail strings "Restart to finish" and "Checking after restart".

## Steps

1. Migration and the state on the install; existing install tests still pass untouched.
2. Exit code mapping and `requiresReboot` in the runner; the confirm endpoint with tests for confirmed, absent and never-restarted.
3. Agent boot-time tracking across a service restart, with the stored value in `agent.json`.
4. Client banner and button; admin filter and count.

## Acceptance criteria

- An installer exiting 3010 leaves the install at "Restart to finish" rather than succeeded, and the client offers the restart.
- After the restart the install turns to succeeded on its own, with no second install and no duplicate row.
- An install whose software is gone after the restart fails with a readable reason.
- Declining the restart leaves the install where it is and changes nothing else.

## Verification

`dotnet test`; VM run with an installer that exits 3010 and a real restart.

## Touches

`src/AppPortal.Server/Installs/InstallService.cs`, `Installs/InstallStore.cs`, `Agent/AgentEndpoints.cs`, `Pages/Admin/Installs/*`, `Data/Migrations/013-reboot-state.sql`, `src/AppPortal.Agent/Jobs/*`, `src/AppPortal.Agent/Executors/Direct*.cs`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/*`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/RebootStateTests.cs`, `tests/AppPortal.Agent.Tests/RebootConfirmationTests.cs`.
