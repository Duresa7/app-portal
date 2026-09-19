# M1-02: Requester identity on installs

**Milestone:** 1 (0.3.0)
**Depends on:** M1-01
**Unlocks:** M1-04, M1-07

## Goal

The server records which signed-in Windows account asked for each install, so admins can see who installed what. The device token still proves the device; the account name is informational and trusted because the PC is managed.

## Context

- The client authenticates with `Authorization: Bearer apd_...` only. `DeviceAuthenticationMiddleware` resolves the device into `HttpContext`.
- Install records carry `DeviceName` only (`src/AppPortal.Shared/Contracts.cs`, `InstallRequest`). The `installs.requested_by` column exists from M1-01 and is NULL.
- Demo mode (`DemoPortalApiClient`) must keep working offline.

## Scope

### In
- Client: on every API call send `X-AppPortal-User` with `Environment.UserDomainName\Environment.UserName`. On a non-domain machine that yields `MACHINE\user`, which is fine.
- Server: middleware reads the header, trims it to 128 characters, rejects control characters, and stores it as `HttpContext.Items["RequestedBy"]`. Missing header means NULL, never an error, so older clients keep working.
- `InstallService.CreateAsync` writes `requested_by`. `InstallRequest` gains `string? RequestedBy` as its last field; the client shows it in Activity as "by DOMAIN\user" when present.
- Demo mode fills the field with the current user.

### Out
- Any verification of the account against a directory. Per-user views across devices.

## Interface

- Header: `X-AppPortal-User: DOMAIN\user`.
- `InstallRequest(..., string? Detail, string? RequestedBy)`.
- `HttpContext.Items["RequestedBy"]` of type `string?`, available to any endpoint after device authentication. M1-04 reuses it.

## Steps

1. Shared contract change; update `InstallRecord.ToPublic()` and the demo client.
2. Client `PortalApiClient`: add the default header once at construction.
3. Server middleware and `InstallService`.
4. Tests: header present is stored and returned; header absent yields null; a header with a newline is rejected with 400.

## Acceptance criteria

- A new client against a new server shows the requester in Activity; an old client against a new server still installs.
- `dotnet test` green on Linux and Windows.

## Verification

`dotnet test`; run the client against the fake-mode server and check `GET /api/v1/installs` output.

## Touches

`src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/Services/PortalApiClient.cs`, `src/AppPortal.Client/Services/DemoPortalApiClient.cs`, `src/AppPortal.Client/ViewModels/ActivityItemViewModel.cs`, `src/AppPortal.Client/Views/*.axaml` (Activity row), `src/AppPortal.Server/Devices/DeviceAuthentication.cs`, `src/AppPortal.Server/Installs/InstallService.cs`, `src/AppPortal.Server/Installs/InstallStore.cs`, `tests/AppPortal.Server.Tests/PortalApiTests.cs`.
