# M5-02: Package managers as one kind

**Milestone:** 5 (0.7.0)
**Depends on:** M3-02, M3-05
**Unlocks:** M5-03, M5-04, M5-05

## Goal

A catalog app can be installed by any of the package managers a Windows fleet already uses: Scoop,
Chocolatey, npm, Bun, pip, Cargo, vcpkg, .NET tools and PowerShell modules. All of them go through
one package kind, one executor and one table of manager descriptions. Adding the eleventh manager is
a row in that table.

## Context

- Every one of these managers is the same shape: find a command line tool, run it with an install
  verb and a package id, read its exit code, watch its output. `IProcessRunner` already does all of
  that, and `ProcessRunner.PumpAsync` already breaks lines on a carriage return as well as a newline,
  which is what makes a redrawn progress bar readable. npm, pip, cargo and scoop all redraw.
- A subtype and an executor per manager would be ten copies of one code path. Each copy also has to
  be threaded through the nine other places a new kind is edited: the JSON attributes, the kind
  switch, the parse error message, `ToPublic`, three parts of the admin form, the agent registration
  and the frozen-shape test. One kind with a `manager` field is the same feature for a tenth of the
  code.
- `winget` stays its own kind. It has a locator for a path SYSTEM cannot see, its own output parser
  and its own exit-code vocabulary, and its JSON shape is frozen by a test and by every catalog that
  exists. M5-01 adds the Microsoft Store to it as a source. Nothing about winget moves here.
- **These managers are not interchangeable with winget.** Scoop, npm, Bun, pip, Cargo and vcpkg
  install developer tools and libraries into a profile or a tree, not applications for everyone on a
  PC. Chocolatey, .NET tools and PowerShell modules can do either. This package makes them all
  expressible; the README must say plainly what each is for, because an administrator who reaches for
  npm to deploy a browser has misunderstood the feature.
- The manager itself will not be on a fresh Windows PC. Two mechanisms already exist and this package
  uses both rather than inventing a third: M3-10 prerequisite chains let an npm app declare Node.js
  as the app that comes first, and M5-03 makes a missing manager a readable failure naming it.

## Scope

### In
- `ManagedPackageDefinition` with kind `managed`: `manager`, `id`, `version`, `extraArgs`, `scope`,
  `requiresReboot`.
- `PackageManagers`, a static table in `AppPortal.Shared` of one descriptor per manager, holding the
  executable to run, the directories to probe when it is not on SYSTEM's PATH, the install, uninstall
  and list argument templates, how a version joins the id, the exit codes that mean "already there",
  the default scope, and one sentence saying what the manager is for.
- Eleven rows: `scoop`, `choco`, `npm`, `bun`, `pip`, `cargo`, `vcpkg`, `dotnet-tool`,
  `powershell-module`, `powershell5-module`, `yarn`.
- **A strict id rule per manager, enforced in `Validate()` before anything reaches a command line.**
  This is the safety requirement of this package and not a nicety. `IProcessRunner` joins arguments
  into one string, and several of these managers are batch files: `npm.cmd`, `scoop.cmd`, `yarn.cmd`.
  Windows runs a batch file through `cmd.exe`, which interprets the ampersand, pipe, caret, percent
  and quote characters in those arguments, so an id carrying one of them is a command rather than a
  package name. The rule is an allowlist of letters, digits and the separators a real package id uses
  for that manager. Never a denylist, and it applies to `version` as well. `extraArgs` stays an
  administrator's own words and is documented as such.
- `ManagedPackageExecutor : IPackageExecutor` with kind `managed`: resolve the descriptor, locate the
  executable, build the command line from the template, run it through `IProcessRunner` for a
  machine-scope job or `IUserSessionLauncher` for a user-scope one, map exit codes, report progress.
  It reports detail beginning with the word Downloading only while the manager says it is
  downloading, because `JobRunner` reads the job's state out of that word.
- `UninstallAsync` on the same executor, from the same table. A manager that reports "not installed"
  as a non-zero code succeeds, the way `WingetExecutor` already treats its own not-installed code.
- An unknown manager fails the job with "This agent does not know the package manager <name>. It is
  likely older than the server.", beside the existing message for an unknown kind.
- Admin catalog page: `managed` joins the kind selector, with a manager dropdown that shows each
  manager's sentence, and the id, version and extra-arguments fields.
- Import, export and `catalog verify` handle the new kind. The hard-coded "needs a kind of winget or
  direct" message in `CatalogStore.Parse` is rewritten to list the kinds from one place.
- `CatalogEntry.ToPublic` stops downcasting to `DirectPackageDefinition` for the download size and
  asks the definition instead, so a kind with no size says so without a cast per kind.

### Out
- Installing the manager itself. An administrator declares it as a prerequisite app.
- Choosing between two managers for one app. `catalog_packages` holds one agent definition per app,
  and "Scoop, or Chocolatey if Scoop is missing" is a second feature with its own failure modes.
- Inventory of what a managed package installed; that is M5-04, and until it lands a managed app does
  not appear under Installed.
- Any change to the Action1 engine or to engine selection.

## Interface

```json
{"kind":"managed","manager":"npm","id":"typescript","version":null,"extraArgs":null,
 "scope":"machine","requiresReboot":false}
```

Property order above is the contract and is frozen by `PackageDefinitionTests`. Manager names are
lower case with a hyphen, exactly as listed, in JSON, the database and the UI.

`PackageManagers.All`, `PackageManagers.Find(name)` and `PackageManagerDescriptor` are the shared
names M5-03, M5-04 and M5-05 build against.

## Steps

1. `PackageManagerDescriptor`, the table, and the id rules. Tests first: one test per manager
   asserting the exact command line for install, install at a pinned version, and uninstall.
2. Id-rule tests: every character the batch-file path makes dangerous is refused, for every manager,
   and the package ids real catalogues use are accepted.
3. `ManagedPackageExecutor` against a fake process runner and `FakeSessions`, covering machine scope,
   user scope, a person not signed in, a missing executable, an already-installed code and a timeout.
4. Definition record, validation, frozen-shape test, import and export round trip.
5. Admin page section; the kind-list message; `ToPublic` without the downcast.
6. README section: what each manager is for, and that a manager must be present or declared as a
   prerequisite.

## Acceptance criteria

- Each of the eleven managers produces the command line its own documentation specifies, asserted
  character for character in a test.
- An id containing an ampersand, a pipe, a caret, a percent, a quote, a space or a newline is refused
  on the admin page with a message naming what is allowed, for every manager.
- A managed app round-trips through export and import unchanged, and an agent that does not know the
  manager fails the job with the readable message rather than retrying three times.
- A user-scope managed install parks with "Waiting for <person> to sign in" and resumes on sign-in,
  through the machinery M3-07 already built and with no new state.

## Verification

`dotnet test`; on the VM, install one package through Chocolatey at machine scope and one through
Scoop at user scope, and take both off again.

## Touches

`src/AppPortal.Shared/Packages.cs`, `src/AppPortal.Shared/PackageManagers.cs` (new),
`src/AppPortal.Agent/Executors/ManagedPackageExecutor.cs` (new), `src/AppPortal.Agent/AgentRun.cs`,
`src/AppPortal.Server/Catalog/CatalogStore.cs`, `Pages/Admin/Catalog/Edit.cshtml*`,
`src/AppPortal.Server/Cli/CatalogCli.cs`, `README.md`,
`tests/AppPortal.Agent.Tests/ManagedPackageExecutorTests.cs` (new),
`tests/AppPortal.Server.Tests/PackageDefinitionTests.cs`, `CatalogStoreTests.cs`.
