# M1-07: Install history pages

**Milestone:** 1 (0.3.0)
**Depends on:** M1-02, M1-03
**Unlocks:** M1-10, M3-05

## Goal

Admins see what was installed across the whole fleet: when, on which device, by whom, through which engine, and whether it succeeded.

## Context

- `installs` has `requested_by` (M1-02) and `engine` (M1-01, always `action1` until M3). `InstallStatusPoller` refreshes active installs every 30 seconds.
- Fleet-wide reads need `InstallStore.ListRecent(filter, limit, offset)`; today only per-device reads exist.

## Scope

### In
- `/admin/installs`: table newest first with columns requested at, device, requested by, app, engine label ("via Action1"), state with percent for active ones, completed at, detail. Filters: device, app, state, requester text, date range. Pagination of 100. The page auto-refreshes the active rows every 30 seconds with htmx polling.
- `/admin/installs/{id}`: one install with every stored field and the external reference.
- Dashboard cards: installs today, failures in the last 7 days, active now.
- `InstallStore.ListRecent` and `CountBy(state, since)` with an index-backed query.

### Out
- Cancelling or retrying an install. Exports.

## Interface

- `ViewData["Nav"] = "installs"`.
- Engine label helper: `EngineLabel.For("action1") == "via Action1"`, `EngineLabel.For("agent") == "via Agent"`, in `AppPortal.Shared` so the client reuses it in M3-05 and M4.

## Steps

1. Store queries with tests for filters and paging.
2. Page, filters as a GET form so URLs are shareable, row partial for polling.
3. Detail page.
4. Dashboard cards.

## Acceptance criteria

- An install started from the client in fake mode appears within 30 seconds, advances through states on the page without reload, and shows the requester.
- Filtering by state=Failed returns only failed rows, verified in a test with seeded data.

## Verification

`dotnet test`; manual run against the fake-mode server with two registered devices.

## Touches

`src/AppPortal.Server/Pages/Admin/Installs/*`, `Installs/InstallStore.cs`, `src/AppPortal.Shared/EngineLabel.cs` (new), `Pages/Admin/Index.cshtml*`, `tests/AppPortal.Server.Tests/InstallStoreQueryTests.cs`, `tests/AppPortal.Server.Tests/AdminInstallsPageTests.cs`.
