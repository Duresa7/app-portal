# M5-04: Managed packages in the installed list

**Milestone:** 5 (0.7.0)
**Depends on:** M5-02
**Unlocks:** M5-06

## Goal

Software a package manager installed appears under Installed, so a person sees that it worked, the
Remove button appears, and a prerequisite that is already there is not installed a second time.

## Context

- Inventory today comes from one command, `winget list`, parsed off its header row by
  `WingetList`. Nothing scoop, npm, pip, cargo or a PowerShell module installs writes an entry under
  the Uninstall registry key, so winget cannot see any of it.
- Three things read that list, and all three are wrong for a managed package until this lands. The
  client's card never turns to Installed, so a person clicks Install again and gets a second job.
  `CatalogEntry.MatchesInstalled` never matches, so Remove never appears. `PrerequisiteResolver`
  treats an installed prerequisite as missing and reinstalls it on every chain.
- `POST /api/v1/agent/software` replaces the device's whole list in one call. A second reporter using
  the same endpoint would erase the first one's rows every time it ran. That is the part of this
  package that needs a schema change rather than another caller.

## Scope

### In
- Each descriptor in `PackageManagers` gains a list command and a parser for its output. The parsers
  are pure functions over captured transcripts, held as fixtures, the way `WingetOutput` already is.
- `device_software` gains a `source` column, defaulting to `winget` so every existing row keeps its
  meaning. The endpoint takes `?source=` and replaces only that source's rows for that device and
  account. An absent `source` means `winget`, so an older agent keeps working unchanged.
- The agent reports each present manager's list after a successful managed install and once a day,
  alongside the winget sweep it already runs. A per-user manager is listed inside that person's
  session, which is where its packages are.
- `GET /api/v1/device/installed` merges every source with the Action1 inventory, and an entry says
  which source found it so an administrator reading the device page can tell.
- Matching: `MatchesInstalled` gains the package id as well as the display name, because a scoop or
  npm package is identified by its id and its "name" is the same string. A managed definition matches
  on its own id first and the app name second.

### Out
- Version comparison or update detection for a managed package. Reporting software no manager
  installed. Removing the winget dependency of the existing sweep.

## Interface

`POST /api/v1/agent/software?account=&source=` where `source` is a manager name, `winget`, or absent
meaning `winget`. `InstalledApp` gains `Source: string`. Migration `021-software-source.sql`.

## Steps

1. List command and parser per descriptor, with a captured transcript fixture for each, tested as
   pure functions.
2. Migration and store change; a test that two sources coexist and that each sweep replaces only its
   own rows.
3. Endpoint parameter and the older-agent default; the merge in the installed endpoint.
4. Agent reporting for each present manager, machine scope and inside a session.
5. Matching by id, and the prerequisite resolver reading it.

## Acceptance criteria

- A package installed through Scoop appears under Installed within one sweep and keeps its Remove
  button, and the winget list is still there afterwards.
- A chain whose prerequisite is already installed through Chocolatey skips that step.
- An agent that posts no `source` still replaces the winget rows and nothing else.

## Verification

`dotnet test`; on the VM, install one package each through Chocolatey and Scoop and confirm both
appear under Installed beside the winget ones.

## Touches

`src/AppPortal.Shared/PackageManagers.cs`, `Contracts.cs`,
`src/AppPortal.Agent/Executors/ManagerList.cs` (new), `Jobs/SoftwareReporter.cs`,
`src/AppPortal.Server/Data/Migrations/021-software-source.sql` (new),
`src/AppPortal.Server/Devices/DeviceSoftwareStore.cs`, `Agent/AgentEndpoints.cs`,
`Installs/InstallService.cs`, `Catalog/CatalogStore.cs`, `Catalog/PrerequisiteResolver.cs`,
`tests/AppPortal.Agent.Tests/Fixtures/*.txt`, plus tests beside each.
