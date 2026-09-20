# M3-10: Software that needs other software first

**Milestone:** 3 (0.5.0)
**Depends on:** M3-01, M3-05
**Unlocks:** M3-06

## Goal

A catalog app can name catalog apps that must be installed before it. Asking for the last one installs the chain in order, as one install the person can follow.

## Context

- A catalog app is one package and one command today. Plenty of software is not: a game needs its launcher, and a great deal of Windows software needs a redistributable runtime that its own installer does not carry.
- Without this, an administrator publishes three apps and writes "install these first, in this order" in the description, and the portal is back to being instructions rather than a button.
- Most of the parts exist. The engine selector picks an engine per app (M3-05), and installed software is known per device (M3-03, M3-07). What is missing is the edge between two apps and a way to show several steps as one install.

## Scope

### In
- Migration 014 adds two tables. `catalog_prerequisites(app_id TEXT NOT NULL REFERENCES catalog_apps(id) ON DELETE CASCADE, requires_app_id TEXT NOT NULL REFERENCES catalog_apps(id), position INTEGER NOT NULL, PRIMARY KEY (app_id, requires_app_id))`. `install_steps(install_id TEXT NOT NULL REFERENCES installs(id), position INTEGER NOT NULL, app_id TEXT NOT NULL, app_name TEXT NOT NULL, engine TEXT NOT NULL, external_ref TEXT, state TEXT NOT NULL, detail TEXT, PRIMARY KEY (install_id, position))`.
- `PrerequisiteResolver.Expand(app, device)` returns the apps to install: depth-first over the edges in `position` order, each app once, the asked-for app last. Apps already in the device inventory are left out. A chain longer than 10 is refused as a configuration error.
- Cycles are refused when the edge is saved, not when somebody installs. The catalog edit page reports which apps form the loop.
- One install row, several steps. The install is running while any step is running, failed as soon as a step fails with the detail naming that step, and succeeded when the last step succeeds. A failed step stops the chain and later steps are not attempted.
- Each step is routed by the engine selector on its own, so a chain may run partly through Action1 and partly through the agent. A step whose app is unavailable on the device makes the whole chain unavailable, with the same reason.
- Client shows the step name, the step number and the count, with the percentage of the running step. Admin install detail lists the steps and their outcomes.
- Import and export carry a `requires` list of app ids, and `catalog verify` refuses unknown ids and cycles.

### Out
- Prerequisites outside the catalog. Version constraints on a prerequisite. Removing a prerequisite when the app that needed it is removed; that is M3-11.

## Interface

The two tables above. `PrerequisiteResolver.Expand`. `InstallRequest` gains `StepName: string?`, `StepNumber: int` and `StepCount: int`. Export field `requires`.

## Steps

1. Tables, resolver and cycle detection, with a table-driven test over diamonds, repeats, already-installed steps and the length limit.
2. `InstallService` creates the steps and advances them. Every existing single-step install becomes a one-step chain, so one code path serves both.
3. Catalog edit page, import and export, `catalog verify`.
4. Client and admin display.

## Acceptance criteria

- An app with two prerequisites installs all three in order from one click, and the client names the step it is on.
- Asking again on a device that already has the first two installs only the last one.
- A failed second step leaves the install failed, naming that step, with the third never started.
- Saving a prerequisite that would close a loop is refused with the loop named.

## Verification

`dotnet test`; manual run in fake mode with a three-app chain.

## Touches

`src/AppPortal.Server/Catalog/PrerequisiteResolver.cs` (new), `Catalog/CatalogStore.cs`, `Installs/InstallService.cs`, `Installs/InstallStore.cs`, `Cli/CatalogCli.cs`, `Pages/Admin/Catalog/Edit.cshtml*`, `Pages/Admin/Installs/Detail.cshtml*`, `Data/Migrations/014-prerequisites.sql`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/*`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/PrerequisiteResolverTests.cs`, `tests/AppPortal.Server.Tests/InstallStepTests.cs`.
