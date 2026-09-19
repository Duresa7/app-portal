# M1-09: Device management pages

**Milestone:** 1 (0.3.0)
**Depends on:** M1-03
**Unlocks:** M1-10, M2-01, M2-02

## Goal

Admins see every enrolled device, its engines, last contact and history, and can rename, disable, rotate the token, or remove it. The `device add` CLI stays for scripts.

## Context

- `devices` columns from M1-01: `action1_endpoint_id`, `has_agent`, `engine_preference`, `enrolled_with_key_id`, `agent_version`, `last_seen_at`.
- `last_seen_at` is not yet written by anything; this package makes `DeviceAuthenticationMiddleware` touch it at most once a minute per device.

## Scope

### In
- `/admin/devices`: table with name, engines (Action1 endpoint present, agent present), agent version, last seen, enabled, enrolled with key, install count. Search by name.
- `/admin/devices/{id}`: details, recent installs and requests for the device, and actions: rename, enable or disable, set or clear the Action1 endpoint id, set per-device engine preference (`action1`, `agent`, or inherit), rotate token (shows once), remove (refused while installs are active; otherwise removes the device and keeps its installs with the name denormalised).
- Add device form for manual registration equal to `device add`.
- Middleware writes `last_seen_at`.

### Out
- Remote actions on the device. Grouping or tagging.

## Interface

- `ViewData["Nav"] = "devices"`.
- `DeviceStore` gains `Update(DeviceRecord)`, `RotateToken(id) -> plaintext`, `TouchLastSeen(id)`, `Remove(id)` returning false when active installs exist.
- Migration 006: `installs.device_name TEXT` denormalised and backfilled, so history survives device removal.

## Steps

1. Migration and store methods with tests.
2. List and detail pages, actions as htmx forms.
3. Middleware touch with an in-memory last-touched cache so it does not write on every request.

## Acceptance criteria

- A disabled device gets 401 on the device API within one request.
- Token rotation invalidates the old token immediately and the new one works.
- Removing a device leaves its installs visible on `/admin/installs` with the device name.

## Verification

`dotnet test`; manual check with the client in fake mode.

## Touches

`src/AppPortal.Server/Pages/Admin/Devices/*`, `Devices/DeviceStore.cs`, `Devices/DeviceAuthentication.cs`, `Installs/InstallStore.cs`, `Data/Migrations/006-device-name-on-installs.sql`, `tests/AppPortal.Server.Tests/DeviceStoreTests.cs`, `tests/AppPortal.Server.Tests/AdminDevicesPageTests.cs`.
