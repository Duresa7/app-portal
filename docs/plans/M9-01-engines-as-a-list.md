# M9-01: Engines as a list

**Milestone:** 9 (0.12.0)
**Depends on:** M3-05
**Unlocks:** M9-02

## Goal

The server treats install engines as a list that any number of engines can join, instead of the two it knows by name. Nothing a person or an administrator sees changes. The Intune engine (M9-02) is then one more registration.

## Context

- `IInstallEngine` (`src/AppPortal.Server/Installs/IInstallEngine.cs`) is already the seam that `AGENTS.md` asks for, and `Program.cs` registers one per engine. But the choice between them is written for two: `EngineSelector.Choose` computes `action1` and `agent` availability by hand, `CatalogEntry.ToPublic` builds the `Engines` array from two booleans, and removal and cancel refuse "not the agent" by name.
- The engine names are checked in many places: the device's engine preference, the app's engine override, the server default, the enrollment key's default engine (`action1`, `agent` or both), and the web and client pages that offer them. There are about 70 references to the two names across 27 files.
- The ladder stays: the device's preference, then the app's, then the server default, then the single engine left, and Action1 first when more than one is left and nothing chose.

## Scope

### In

- **`IInstallEngine`** gains:
  - `string DisplayName` ("Action1", "Agent").
  - `bool CanInstall(DeviceRecord device, CatalogEntry app)`: Action1 when the app has an Action1 package and the device an endpoint; the agent when the app has an agent package and the device has the agent.
  - `bool CanStop` and `bool CanRemove`: false for Action1, true for the agent. They replace the checks by name in `InstallService.Cancel` and `UninstallAsync`, and the messages name the engine: "{app} is being installed by {engine} and has to be stopped there."
  - `int Rank`: the order used when more than one engine is left and nothing chose. Action1 1, agent 2.
- **`EngineRegistry`**, a singleton over the registered engines: `Find(name)`, `All`, `Names`, and `IsKnown(name)`. Every check of an engine name uses it.
- **`EngineSelector.Choose(device, app, serverDefault, registry)`** follows the ladder over the engines that `CanInstall`.
- **`CatalogEntry.ToPublic(registry)`** lists the engines that could ever run the app: those whose package the app has.
- **Pages and API:** the engine lists in the settings page, the device page, the catalog editor and the key form come from the registry, web and client. The client gets them from `GET /api/v1/admin/engines`, `IReadOnlyList<AdminEngine>`, and falls back to Action1 and Agent against an older server.
- **Enrollment keys** keep `action1`, `agent` and `both`. A key names the engines a PC enrolled with it has, and only those two come from enrollment.
- `EngineLabel.For` stays and keeps working for names it does not know.

### Out

- Any new engine. M9-02 adds Intune.
- Changing the ladder or its order.

## Interface

```csharp
public interface IInstallEngine
{
    string Name { get; }
    string DisplayName { get; }
    int Rank { get; }
    bool CanStop { get; }
    bool CanRemove { get; }
    bool CanInstall(DeviceRecord device, CatalogEntry app);
    Task<string> StartAsync(DeviceRecord device, CatalogEntry app, PackageDefinition? definition, InstallRecord install, CancellationToken ct);
    Task<InstallRecord> RefreshAsync(InstallRecord install, CancellationToken ct);
}

public sealed record AdminEngine(string Name, string DisplayName);
```

## Steps

1. Characterisation tests first, before any change, for every row of the ladder with the two engines, for `ToPublic`'s engine lists, and for the cancel and removal refusals. They must pass before and after.
2. The interface, the registry, the selector, `ToPublic`, the service.
3. Each page and endpoint that checks an engine name, one by one, with its tests.
4. `GET /api/v1/admin/engines` and the client's use of it.

## Acceptance criteria

- The characterisation tests pass unchanged.
- A test engine registered only in a test (`Name` "test") can be chosen by the ladder, is listed by `ToPublic` for an app that has its package, and appears in the settings page's list, without a change to any code outside the test.
- Format, build and all tests pass.

## Touches

`src/AppPortal.Server/Installs/*.cs`, `src/AppPortal.Server/Catalog/CatalogStore.cs`, `src/AppPortal.Server/Program.cs`, `src/AppPortal.Server/Settings/SettingsStore.cs`, `src/AppPortal.Server/Devices/DeviceStore.cs`, `src/AppPortal.Server/Admin/Api/*.cs`, `src/AppPortal.Server/Pages/Admin/**`, `src/AppPortal.Shared/AdminContracts.cs`, `src/AppPortal.Client/Services/{AdminApiClient,DemoAdminApiClient}.cs`, `src/AppPortal.Client/ViewModels/Admin/*.cs`, tests.
