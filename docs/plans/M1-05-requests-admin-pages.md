# M1-05: Requests admin pages

**Milestone:** 1 (0.3.0)
**Depends on:** M1-03, M1-04
**Unlocks:** M1-10

## Goal

Admins see pending requests, approve or deny each with an optional reason, and can look back at decided ones.

## Context

- Layout, auth policy and htmx conventions come from M1-03. `AppRequestStore.ListByStatus` and `Decide` come from M1-04.
- The user sees the decision and reason in the client on its next poll; nothing else to notify.

## Scope

### In
- `/admin/requests`: tabs Pending (default), Approved, Denied, All. Table columns: submitted, device, requested by, text, and for decided rows the decision, reason, decided by, decided at. Pagination of 50.
- Each pending row has Approve and Deny buttons that reveal a reason field (optional, 500 characters) and confirm. The row updates in place via htmx; the pending count in the navigation badge updates.
- Dashboard card: pending requests count, linking to the page.
- A decided request cannot be re-decided; the store returns false and the page shows a notice if two admins race.

### Out
- Linking a request to a catalog app. Bulk actions. Comments or threads.

## Interface

None new beyond routes `/admin/requests` and its handlers `OnPostApprove`, `OnPostDeny`. `ViewData["Nav"] = "requests"`.

## Steps

1. Page model with tab and paging query parameters; partial for the table and for one row.
2. Approve and deny handlers writing `decided_by` from `IAdminContext.Username`.
3. Navigation badge count via a small view component queried per request.
4. Tests: only admins reach the page; approve stores status, reason and decider; a second decision is refused.

## Acceptance criteria

- A request submitted from the client appears under Pending within one refresh, and the decision appears in the client after the next poll with the reason text.
- Works with JavaScript disabled (full page reload path) and with htmx (in-place).

## Verification

`dotnet test`; end-to-end by hand with the client in fake mode.

## Touches

`src/AppPortal.Server/Pages/Admin/Requests/*`, `Pages/Admin/Shared/_RequestRow.cshtml`, `Pages/Admin/_Layout.cshtml` (badge), `Pages/Admin/Index.cshtml*` (card), `tests/AppPortal.Server.Tests/AdminRequestsPageTests.cs`.
