# M1-03: Admin accounts and web shell

**Milestone:** 1 (0.3.0)
**Depends on:** M1-01
**Unlocks:** M1-05, M1-06, M1-07, M1-08, M1-09

## Goal

Admins exist, can sign in to `/admin` in a browser, and every later admin page drops into a shared layout. The same session store also issues bearer tokens for the client's admin mode in milestone 4, so it is designed here once.

## Context

- The server is a minimal API in `Program.cs` with no Razor, no auth services. `DeviceAuthenticationMiddleware` guards `/api/v1/*` only.
- The web UI is Razor Pages with htmx for partial updates. No Node, no bundler. Serve htmx from `wwwroot` as a checked-in minified file with its version in the filename.
- Directory-agnostic: local accounts with hashed passwords. OIDC is deferred.

## Scope

### In
- Migration 002: tables in *Interface*.
- Password hashing with `PasswordHasher<T>` from `Microsoft.AspNetCore.Identity` (PBKDF2, no Identity framework otherwise).
- CLI: `admin add --username <name>` prompts for a password (or reads `APPPORTAL_ADMIN_PASSWORD` for scripts), `admin reset-password --username`, `admin list`, `admin disable --username`. First-run message in the log when no admin exists: how to create one.
- Cookie authentication for `/admin/*`: sign-in page, sign-out, 8-hour sliding session stored server-side in `admin_sessions` (kind `web`) so revocation works. Anti-forgery on every form.
- Bearer admin tokens: `Authorization: Bearer apa_...` resolved against `admin_sessions` (kind `api`). Issued by `POST /api/v1/admin/session {username,password}` returning `{token, expiresAt}`; revoked by `DELETE /api/v1/admin/session`. Only these two endpoints exist in this package; M4-01 adds the rest.
- Layout: left navigation (Dashboard, Installs, Catalog, Requests, Devices, Enrollment keys, Admins), signed-in name, sign out. Fluent-like plain CSS in one file, light and dark by `prefers-color-scheme`.
- Pages: `/admin/login`, `/admin` (dashboard placeholder with counts: devices, installs today, pending requests placeholder 0), `/admin/admins` (list, add, disable, reset password).
- Rate limit sign-in: 10 failures per username per 15 minutes, then 429.

### Out
- Every other page (each has its own package). OIDC. Password complexity rules beyond a 12-character minimum.

## Interface

```sql
admins(id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
       password_hash TEXT NOT NULL, disabled INTEGER NOT NULL DEFAULT 0,
       created_at TEXT NOT NULL, last_login_at TEXT);
admin_sessions(token_hash TEXT PRIMARY KEY, admin_id TEXT NOT NULL REFERENCES admins(id),
       kind TEXT NOT NULL,            -- 'web' | 'api'
       created_at TEXT NOT NULL, expires_at TEXT NOT NULL, last_used_at TEXT);
```

- Authorization policy name `Admin`; pages use `[Authorize(Policy = "Admin")]`; API endpoints use `.RequireAuthorization("Admin")`.
- `IAdminContext` (scoped) exposing `AdminId`, `Username` for pages and endpoints.
- Layout file `Pages/Admin/_Layout.cshtml`; every admin page sets `Layout = "_Layout"` and a `ViewData["Nav"]` key from: `dashboard`, `installs`, `catalog`, `requests`, `devices`, `keys`, `admins`.
- htmx conventions: partial views under `Pages/Admin/Shared/`; forms post to the same page handler and return the partial when `HX-Request` is present.

## Steps

1. Add Razor Pages and auth services; keep the minimal API device routes untouched.
2. Migration, `AdminStore`, `AdminSessionStore`, hashing, CLI.
3. Cookie scheme backed by the session table; bearer scheme for `apa_` tokens; one policy accepting both.
4. Layout, CSS, login page, dashboard, admins page.
5. Tests with `WebApplicationFactory`: login succeeds and sets the cookie; wrong password fails and rate limits; `/admin` without a session redirects; bearer session issues and revokes; disabled admin cannot sign in.

## Acceptance criteria

- `admin add` then browser sign-in works against the Docker image with no other configuration.
- All admin routes are unreachable without a session; device routes unaffected.
- Smoke test extended: `admin add` inside the container, then `POST /api/v1/admin/session` returns a token and `GET /admin` with the cookie returns 200.

## Verification

`dotnet test`; `deploy/smoke-test.sh`; manual sign-in in a browser in light and dark mode.

## Touches

`src/AppPortal.Server/AppPortal.Server.csproj`, `Program.cs`, new `Admin/` (AdminStore.cs, AdminSessionStore.cs, AdminAuth.cs), new `Pages/Admin/**`, new `wwwroot/admin/**`, `Cli/AdminCli.cs`, `Data/Migrations/002-admins.sql`, `deploy/smoke-test.sh`, `tests/AppPortal.Server.Tests/AdminAuthTests.cs`.
