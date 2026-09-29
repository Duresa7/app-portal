# M8-03: Anti-cheat on the device

**Milestone:** 8 (0.11.0)
**Depends on:** M2-02
**Unlocks:** M8-04, M8-08

## Goal

The agent reports which kernel anti-cheat services and drivers a PC carries, and whether each is running. An administrator sees them on the device page, in the web admin and in the client, and can answer "why will this game not start" without a remote session.

## Context

- Competitive games install their own anti-cheat, and much of it runs as a Windows service or a kernel driver: Riot Vanguard, Easy Anti-Cheat, BattlEye, FACEIT, EA Javelin, PunkBuster, nProtect GameGuard, XIGNCODE3 and HoYoverse's mhyprot. A stopped or half-installed one is the usual reason a game refuses to start, and Vanguard in particular needs a restart after it is installed.
- They do not appear in winget's list, because most have no uninstall entry of their own. The service control manager lists all of them, services and drivers, by service name.
- This is inventory, not a check. The roadmap's **Requirements** decision stands: the portal never refuses an install because of what it finds, and it reads no TPM or Secure Boot state.
- The agent already reports what package managers a device has on start and once a day (`ManagerReporter`, M5-03). This report uses the same cadence.

## Scope

### In

- **`AntiCheats`** in `src/AppPortal.Shared/AntiCheats.cs`: one row per known product, with its display name and the service names that belong to it.

  | Product | Services and drivers |
  |---|---|
  | Riot Vanguard | `vgc`, `vgk` |
  | Easy Anti-Cheat | `EasyAntiCheat`, `EasyAntiCheat_EOS` |
  | BattlEye | `BEService`, `BEDaisy` |
  | FACEIT Anti-Cheat | `FACEIT`, `FACEITService` |
  | EA Javelin Anticheat | `EAAntiCheatService` |
  | PunkBuster | `PnkBstrA`, `PnkBstrB` |
  | nProtect GameGuard | `npggsvc` |
  | XIGNCODE3 | `xhunter1` |
  | HoYoverse anti-cheat | `mhyprot2`, `mhyprot3` |

- **`AntiCheatReporter`** in `src/AppPortal.Agent/Jobs/`, a hosted service: on start and once a day, lists services and drivers through `IServiceInventory` (Windows: `ServiceController.GetServices()` and `GetDevices()`), keeps the known ones, and posts them. It posts an empty list too, so that a removed product leaves the record.
- **Route** `POST /api/v1/agent/anticheat`, body `[{ "product", "service", "type": "service"|"driver", "state": "running"|"stopped"|..., "startType": "automatic"|"manual"|"disabled"|"boot"|"system" }]`, at most 64 rows, replacing the device's rows.
- **Table** `device_anticheat (device_id, service, product, type, state, start_type, seen_at)`, migration 023.
- **Admin:** the device detail page in the web admin and the client lists them under "Anti-cheat" with product, state and start type; "Stopped" and "Disabled" are marked. `AdminDeviceDetail` gains `IReadOnlyList<DeviceAntiCheat>? AntiCheats`.
- **Docs:** `docs/administration.md` (what the section shows and that nothing acts on it) and `docs/api.md`.

### Out

- Starting, stopping or repairing an anti-cheat service. The game's launcher owns it.
- Anti-cheat that runs only while a game runs and installs no service.
- Showing anti-cheat to the person in the client. It is support information for an administrator.

## Interface

```csharp
public sealed record AntiCheatProduct(string Name, IReadOnlyList<string> Services);
public sealed record DeviceAntiCheat(string Product, string Service, string Type, string State, string StartType);
```

`AntiCheats.Find(string service)` matches without regard to case.

## Steps

1. Tests first: the reporter over a fake service inventory (known and unknown services, a driver and a service of one product, an empty result posts an empty list, a not-enrolled device posts nothing), the endpoint (limit, replace, a device sees only its own), the store.
2. Migration, store, endpoint, reporter, registration.
3. Web and client device pages, contracts, demo data, docs.

## Acceptance criteria

- On a PC with a known anti-cheat installed, the device page lists it with its state within a minute of the agent starting.
- Uninstalling it and restarting the agent removes it from the page.
- Format, build and all tests pass on Linux and on the Windows leg.

## Verification

Tests as above, and a Windows-only test that `IServiceInventory` lists at least one known Windows service (for example `EventLog`) with its state.

## Touches

`src/AppPortal.Shared/AntiCheats.cs` (new), `src/AppPortal.Shared/AdminContracts.cs`, `src/AppPortal.Agent/Jobs/AntiCheatReporter.cs` (new), `src/AppPortal.Agent/AgentRun.cs`, `src/AppPortal.Server/Data/Migrations/023-device-anticheat.sql` (new), `src/AppPortal.Server/Devices/DeviceAntiCheatStore.cs` (new), `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `src/AppPortal.Server/Admin/Api/AdminDeviceEndpoints.cs`, `src/AppPortal.Server/Pages/Admin/Devices/Detail.cshtml*`, `src/AppPortal.Server/Program.cs`, `src/AppPortal.Client/ViewModels/Admin/DevicesViewModel.cs`, `src/AppPortal.Client/Views/Admin/DevicesView.axaml`, `src/AppPortal.Client/Services/DemoAdminApiClient.cs`, `docs/administration.md`, `docs/api.md`, tests.
