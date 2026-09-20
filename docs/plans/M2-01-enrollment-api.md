# M2-01: Enrollment API

**Milestone:** 2 (0.6.0)
**Depends on:** M1-08, M1-09
**Unlocks:** M2-05

## Goal

A PC turns an enrollment key into a device record and a device token in one call, so installers never carry device tokens.

## Context

- `EnrollmentKeyStore.TryConsume` (M1-08) atomically validates and counts a use. `DeviceStore` (M1-09) creates devices with engine flags.
- Device names are unique. Re-enrolling the same PC (reimage, reinstall) must not fail on the name.

## Scope

### In
- `POST /api/v1/enroll` with body `{key, deviceName, action1EndpointId?, agentVersion?, machineId}` and no bearer token. Answers 201 `{deviceId, deviceToken, deviceName, engines: ["action1","agent"]}`; 401 for an unknown, expired, exhausted or revoked key; 400 for a bad body. Rate limited to 30 per minute per source address.
- `machineId` is a stable hardware id the installer computes (Windows `MachineGuid`). If a device with the same `machineId` exists, the call rotates its token and updates its name and flags instead of creating a duplicate. Migration 007 adds `devices.machine_id TEXT UNIQUE`.
- Engines assigned from the key's `default_engine`: `agent` or `both` sets `has_agent = 1`; `action1` or `both` requires `action1EndpointId`, otherwise 400 with a clear message.
- `GET /api/v1/enroll/check` with header `X-Enrollment-Key` answers 204 when the key is currently usable, 401 otherwise, and does not consume a use. The wizard calls this before running the MSI.
- Audit: `enrollment_events` table (key id, device id, source address, outcome, at) and a list on `/admin/keys/{id}`.

### Out
- Any client-side code. Approval queues for enrollment.

## Interface

Routes and body shapes above. `devices.machine_id`. `enrollment_events(id, key_id, device_id, source, outcome, created_at)`.

## Steps

1. Migration, endpoint, rate limit, tests (each outcome, re-enrollment by machine id, concurrent last use).
2. Admin key detail page section listing events.
3. Smoke test: create a key via CLI, enroll with curl, use the returned token on the catalog route.

## Acceptance criteria

- Enrollment with a valid key yields a token that works on `/api/v1/catalog`.
- A second enrollment with the same `machineId` returns a new token and the old one stops working.
- Neither the key nor the token appears in server logs.

## Verification

`dotnet test`; `deploy/smoke-test.sh`.

## Touches

`src/AppPortal.Server/Enrollment/EnrollmentEndpoints.cs` (new), `Enrollment/EnrollmentEventStore.cs` (new), `Devices/DeviceStore.cs`, `Pages/Admin/Keys/Detail.cshtml*`, `Data/Migrations/007-enrollment.sql`, `src/AppPortal.Shared/Contracts.cs`, `deploy/smoke-test.sh`, `tests/AppPortal.Server.Tests/EnrollmentApiTests.cs`.
