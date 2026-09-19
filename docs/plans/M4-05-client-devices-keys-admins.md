# M4-05: Client admin, devices, keys, admins, settings

**Milestone:** 4 (0.6.0)
**Depends on:** M4-02
**Unlocks:** M4-06

## Goal

The remaining admin areas reach parity in the client: devices, enrollment keys, admin accounts, and settings.

## Context

- Endpoints from M4-01. Show-once secrets (new enrollment key, rotated device token) must be displayed in a dialog with a copy button and never logged or persisted by the client.

## Scope

### In
- Devices page: grid (name, engines, agent version, last seen, enabled, key, installs) with search; detail view with recent installs and requests; actions rename, enable or disable, endpoint id, engine preference, rotate token (show-once dialog), remove (with the active-install refusal); Add device form.
- Enrollment keys page: grid with status; Create dialog (name, expiry, max uses, engine) ending in the show-once dialog; Revoke with confirmation; events list per key.
- Admins page: list, add (with a generated initial password shown once or an entered one), disable, reset password. An admin cannot disable their own account.
- Settings page: default engine with the rule explanation.
- Demo admin data for all four.

### Out
- Anything the web UI does not do.

## Interface

None new.

## Steps

1. View models with tests over a fake client, including the self-disable guard.
2. Views; the show-once dialog as one shared component.
3. Demo data.

## Acceptance criteria

- A key created in the client enrolls a PC through the wizard.
- A token rotated in the client invalidates the old one immediately (the device's next request fails and recovers after re-enrollment or manual update).

## Verification

`dotnet test`; manual against fake mode; screenshots.

## Touches

`src/AppPortal.Client/ViewModels/Admin/{Devices,Keys,Admins,Settings}ViewModel.cs`, `Views/Admin/{Devices,Keys,Admins,Settings}View.axaml*`, `Views/Admin/ShowOnceDialog.axaml*`, `Services/DemoAdminApiClient.cs`, `tests/AppPortal.Client.Tests/Admin*.cs`.
