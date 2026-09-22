# M5-03: Which package managers a device has

**Milestone:** 5 (0.7.0)
**Depends on:** M5-02
**Unlocks:** M5-06

## Goal

An administrator can see which package managers each PC has before putting an app in the catalog that
needs one, and a device that lacks the manager fails the install with a sentence that says so.

## Context

- None of the managers in M5-02 are on a fresh Windows PC. Chocolatey, Scoop, Node, Python, Rust and
  PowerShell 7 all arrive because somebody installed them.
- Without this, an administrator adds an npm app and every device answers "npm is not installed on
  this PC", with no way to find out beforehand how many devices that will be.
- The fix for a missing manager already exists and this package does not duplicate it. M3-10 lets an
  app name the catalog apps that come first, so an npm package declares Node.js as a prerequisite and
  the chain installs Node before it. This package makes the gap visible so an administrator knows to
  do that.
- Apps are still offered to a device that lacks the manager. Hiding them would make an app appear and
  disappear from a person's list as a report arrives, and the first report arrives after enrollment,
  not during it.

## Scope

### In
- Agent: on service start, and once a day after that, run each descriptor's detection and report the
  managers it found with their versions. Detection is locating the executable and asking it for its
  version, with a short timeout, and a manager that is absent or that fails is simply not reported.
- `POST /api/v1/agent/managers` taking `[{name, version}]` and replacing the device's list, in the
  shape `POST /api/v1/agent/software` already uses.
- Migration: `device_managers(device_id, manager, version, seen_at)`, primary key on the first two,
  rows removed with the device.
- Admin device page: a "Package managers" list with each name, version and when it was last seen, and
  the sentence "None reported yet" before the first report.
- Admin catalog page: when the agent package is `managed`, a line under the manager dropdown saying
  how many enrolled devices report that manager, and naming the prerequisite mechanism when the count
  is zero.
- Job failure: `ManagedPackageExecutor` reports "Chocolatey is not installed on this PC. An
  administrator can add it as a prerequisite of this app." using the descriptor's display name.
- Admin JSON API: the managers list on `GET /devices/{id}`, so M4-05 can show it in the client.

### Out
- Installing a manager automatically. Refusing an install because of a missing manager, beyond the
  failure the job already reports. Reporting managers for a per-user scope separately; a manager in a
  profile is M5-04's problem, not this one's.

## Interface

`POST /api/v1/agent/managers` body `[{"name":"choco","version":"2.2.2"}]`, replacing the whole list.
`device_managers` as above. `AdminDevice` gains `Managers: DeviceManager[]`.

## Steps

1. Detection in `PackageManagers`, one probe per descriptor, tested against a fake process runner and
   a fake directory layout.
2. Reporter hosted service in the agent, beside the software reporter it copies.
3. Migration, store, endpoint, tests.
4. Device page section and catalog page count.
5. Executor message using the display name.

## Acceptance criteria

- A device reports every manager it has and none that it does not, proved against a fake layout.
- The device page lists them, and lists nothing without an error before the first report.
- An install for a manager the device has not reported fails with the named sentence, not a raw exit
  code, and does not retry three times.
- Removing a device removes its rows.

## Verification

`dotnet test`; on the VM, install Chocolatey by hand and watch it appear on the device page.

## Touches

`src/AppPortal.Shared/PackageManagers.cs`, `Contracts.cs`, `AdminContracts.cs`,
`src/AppPortal.Agent/Jobs/ManagerReporter.cs` (new), `AgentRun.cs`,
`src/AppPortal.Agent/Executors/ManagedPackageExecutor.cs`,
`src/AppPortal.Server/Data/Migrations/020-device-managers.sql` (new),
`src/AppPortal.Server/Devices/*`, `Agent/AgentEndpoints.cs`, `Admin/Api/AdminDeviceEndpoints.cs`,
`Pages/Admin/Devices/Detail.cshtml*`, `Pages/Admin/Catalog/Edit.cshtml*`, plus tests beside each.
