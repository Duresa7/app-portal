# M4-02: Client admin sign-in and navigation

**Milestone:** 4 (0.6.0)
**Depends on:** M4-01
**Unlocks:** M4-03, M4-04, M4-05

## Goal

An admin signs in inside the Windows client and gets an Admin area with its own navigation, on top of the user sections. The token is stored per Windows user, not per device.

## Context

- The client talks to the server through `IPortalApiClient` with the device token. Admin calls need a second client with the `apa_` token, `AdminApiClient`, against the same base URL.
- Store the admin token with DPAPI for the current user under `%LocalAppData%\AppPortal\admin-session.bin`; never in `client.json`.
- The Windows 11 NavigationView pattern already exists; the admin area adds a divider and a second group of items in the same pane, shown only when signed in.

## Scope

### In
- `AdminApiClient` in `Services/` covering the endpoints of M4-01 with typed methods and the shared contracts; unit-tested with a fake handler.
- Sign-in: an "Admin" footer item opens a sign-in dialog (username, password); on success the pane shows the admin group: Dashboard, Installs, Catalog, Requests, Devices, Enrollment keys, Admins, Settings, plus Sign out. Session persists across client restarts until revoked or expired; 401 anywhere signs out with a notice.
- Dashboard page from `GET /dashboard` with the same cards as the web.
- Navigation and page scaffolding for the remaining pages with "Coming in this release" placeholders so M4-03 to M4-05 only fill views.
- Demo mode: a demo admin account `admin` / `demo` with in-memory data so screenshots and UI work without a server.

### Out
- Any admin page content beyond the dashboard.

## Interface

- `AdminApiClient` public surface, `IAdminSession` (`IsSignedIn`, `Username`, `SignInAsync`, `SignOutAsync`).
- `MainViewModel.SelectedSection` indices 10 and up are admin pages, so `--screenshot` can render them.

## Steps

1. Client and session storage with DPAPI; tests for the storage round-trip on Windows and a no-op fallback on Linux (development only).
2. Sign-in dialog and pane changes following the existing styles.
3. Dashboard page.
4. Placeholders and demo admin data.

## Acceptance criteria

- Sign in, close the client, reopen: still signed in. Revoke the session on the web: next click signs out with a message.
- A standard user cannot find admin data on disk in readable form.

## Verification

`dotnet test`; manual on Windows; `--demo --screenshot a.png 10`.

## Touches

`src/AppPortal.Client/Services/AdminApiClient.cs` (new), `Services/AdminSession.cs` (new), `Services/DemoAdminApiClient.cs` (new), `ViewModels/Admin/*` (new), `Views/Admin/*` (new), `Views/MainWindow.axaml`, `ViewModels/MainViewModel.cs`, `tests/AppPortal.Client.Tests/**` (new project).
