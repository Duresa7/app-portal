# M4-03: Client admin, installs and requests

**Milestone:** 4 (0.7.0)
**Depends on:** M4-02
**Unlocks:** M4-06

## Goal

Inside the client, admins browse fleet-wide install history and decide app requests with the same capabilities as the web pages.

## Context

- Data comes from `AdminApiClient` (M4-02) over the M4-01 endpoints. Contracts are shared, so no mapping layer.
- The client already renders lists with `DataGrid` (Activity). Reuse it; keep the Windows 11 look.

## Scope

### In
- Installs page: grid with the same columns and filters as the web page (device, app, state, requester, date range), paging, auto-refresh of active rows every 30 seconds, and a detail flyout for one install.
- Requests page: tabs Pending, Approved, Denied, All; approve and deny with an optional reason in a dialog; optimistic row update with rollback on error; a pending count badge on the navigation item.
- Demo admin data for both.

### Out
- Exports. Bulk actions.

## Interface

None new.

## Steps

1. View models with unit tests over a fake `AdminApiClient`.
2. Views following the existing styles; keyboard navigation works.
3. Demo data.

## Acceptance criteria

- Approving a request in the client is visible on the web page immediately and in the requesting device's client on its next poll.
- Filters produce the same results as the web page for the same inputs, checked against the API tests' seeded data.

## Verification

`dotnet test`; manual against fake mode; screenshots of both pages in light and dark.

## Touches

`src/AppPortal.Client/ViewModels/Admin/InstallsViewModel.cs`, `ViewModels/Admin/RequestsViewModel.cs`, `Views/Admin/InstallsView.axaml*`, `Views/Admin/RequestsView.axaml*`, `Services/DemoAdminApiClient.cs`, `tests/AppPortal.Client.Tests/Admin*.cs`.
