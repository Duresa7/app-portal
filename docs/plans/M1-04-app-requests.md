# M1-04: App requests, API and client

**Milestone:** 1 (0.3.0)
**Depends on:** M1-02
**Unlocks:** M1-05, M1-10

## Goal

A user types what they want in a text box and submits it. The request is stored with the device and the account, and the user sees each request with its status and, once decided, the admin's reason.

## Context

- Client sections today: Apps, Installed, Activity (`MainViewModel.SelectedSection` 0, 1, 2). Navigation items live in `Views/MainWindow.axaml`.
- `HttpContext.Items["RequestedBy"]` is set by M1-02.
- Demo mode must show sample requests and accept new ones in memory.

## Scope

### In
- Migration 003: `app_requests` table.
- Device API: `POST /api/v1/requests {text}` answers 201 with the record; 400 when text is empty or over 500 characters; 429 when the device has more than 20 pending requests. `GET /api/v1/requests` lists this device's requests newest first.
- Client: a fourth section, **Requests**, with a text box, a Submit button, and a list of the device's requests showing text, submitted date, status badge (Pending, Approved, Denied) and the reason when present. Navigation item with an icon consistent with the others. Demo data.
- Poll requests on the same refresh cadence as installs.

### Out
- Admin decisions (M1-05). Editing or withdrawing a request. Attachments, links, categories.

## Interface

```sql
app_requests(id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES devices(id),
  requested_by TEXT, text TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',   -- 'pending' | 'approved' | 'denied'
  reason TEXT, decided_by TEXT, decided_at TEXT,
  created_at TEXT NOT NULL);
CREATE INDEX app_requests_status_created ON app_requests(status, created_at DESC);
CREATE INDEX app_requests_device_created ON app_requests(device_id, created_at DESC);
```

Shared contracts:

```csharp
public sealed record AppRequest(string Id, string Text, string DeviceName, string? RequestedBy,
    AppRequestStatus Status, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);
public enum AppRequestStatus { Pending, Approved, Denied }
public sealed record CreateAppRequest(string Text);
```

`AppRequestStore` with `Create`, `ListForDevice`, `ListByStatus(status, limit, offset)`, `Decide(id, status, reason, decidedBy)`; M1-05 uses the last two.

## Steps

1. Migration, store, contracts, endpoints, tests (create, list scoped to the calling device, limits).
2. Client view model `RequestsViewModel` or extension of `MainViewModel`, view, navigation, demo client.
3. `--screenshot` section index 3 renders Requests, so a documentation image can be made.

## Acceptance criteria

- Device A cannot see device B's requests.
- Submitting shows the new request at the top without a manual refresh.
- Screenshot flag renders the Requests section on the Windows runner.

## Verification

`dotnet test`; client against the fake-mode server; `AppPortal.exe --demo --screenshot r.png 3`.

## Touches

`src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Server/Requests/` (new), `Api/PortalEndpoints.cs`, `Data/Migrations/003-requests.sql`, `src/AppPortal.Client/ViewModels/*`, `Views/MainWindow.axaml`, `Services/IPortalApiClient.cs`, `Services/PortalApiClient.cs`, `Services/DemoPortalApiClient.cs`, `tests/AppPortal.Server.Tests/RequestsApiTests.cs`.
