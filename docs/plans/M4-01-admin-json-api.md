# M4-01: Admin JSON API and client admin sessions

**Milestone:** 4 (0.6.0)
**Depends on:** M1-10
**Unlocks:** M4-02

## Goal

Everything the web admin UI can do is available as a JSON API under `/api/v1/admin/` with bearer admin tokens, so the Windows client can offer full admin parity without screen-scraping pages.

## Context

- M1-03 created the `apa_` bearer scheme, the `Admin` policy, and the session endpoints. The Razor Pages call store methods directly; this package exposes the same store methods over HTTP and leaves the pages alone.
- Contracts go in `AppPortal.Shared` so the client compiles against them.

## Scope

### In
- Endpoints, all `RequireAuthorization("Admin")`, JSON in and out, paging by `limit` and `offset`, consistent `ErrorMessage` bodies:
  - Installs: `GET /installs` with the same filters as the page, `GET /installs/{id}`.
  - Requests: `GET /requests?status=`, `POST /requests/{id}/approve {reason}`, `POST /requests/{id}/deny {reason}`.
  - Catalog: `GET /catalog`, `GET /catalog/{id}`, `PUT /catalog/{id}`, `DELETE /catalog/{id}`, `POST /catalog/{id}/hidden {hidden}`, `POST /catalog/import`, `GET /catalog/export`, `POST /catalog/action1/search {term}`, `POST /catalog/action1/verify {packageId, version}`.
  - Devices: `GET /devices`, `GET /devices/{id}`, `PUT /devices/{id}`, `POST /devices/{id}/rotate-token`, `DELETE /devices/{id}`, `POST /devices {name, action1EndpointId?}`.
  - Keys: `GET /keys`, `POST /keys`, `POST /keys/{id}/revoke`, `GET /keys/{id}/events`.
  - Admins: `GET /admins`, `POST /admins`, `POST /admins/{id}/disable`, `POST /admins/{id}/reset-password`.
  - Settings: `GET /settings`, `PUT /settings`.
  - Dashboard: `GET /dashboard` with the counts the web dashboard shows.
- Session hardening for the client: `POST /api/v1/admin/session` accepts `{username, password, deviceName}` and records the device name on the session; `GET /api/v1/admin/sessions` lists and `DELETE /api/v1/admin/sessions/{id}` revokes; expiry 30 days sliding for `api` kind.
- Request logging of admin API calls at Information with admin username and route, never bodies.

### Out
- Any client code. Any new capability the web UI lacks.

## Interface

Routes above. Shared contracts: `AdminInstall`, `AdminRequest`, `AdminCatalogApp` (full definition), `AdminDevice`, `EnrollmentKeySummary`, `EnrollmentKeyCreated` (with the plaintext, once), `AdminAccount`, `PortalSettings`, `DashboardCounts`, all in `AppPortal.Shared/AdminContracts.cs`.

## Steps

1. Contracts file.
2. Endpoint groups, one file per area, each a thin adapter over the existing stores.
3. Tests: each endpoint has at least one authorised and one unauthorised test; a device token is rejected on admin routes; a disabled admin's token stops working.

## Acceptance criteria

- Every action available in the web UI has an endpoint here, checked by a table in the pull request description.
- Smoke test drives the request approve flow through the API instead of the page handler.

## Verification

`dotnet test`; smoke test.

## Touches

`src/AppPortal.Shared/AdminContracts.cs` (new), `src/AppPortal.Server/Admin/Api/*.cs` (new), `Admin/AdminSessionStore.cs`, `Program.cs`, `deploy/smoke-test.sh`, `tests/AppPortal.Server.Tests/AdminApiTests.cs`.
