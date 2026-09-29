# M9-02: Intune engine

**Milestone:** 9 (0.12.0)
**Depends on:** M9-01
**Unlocks:** M9-05

## Goal

Microsoft Intune becomes a third install engine, beside Action1 and the agent. An administrator links a catalog app to an app that already exists in Intune. When a person asks for it on a PC that Intune manages, the portal makes Intune install it on that one PC, follows it to the end, and reports it like every other install, labelled "via Intune". Removal works the same way.

## Context

- Intune installs an app on a device only through an **assignment** to a group. There is no call that says "install app A on device D". The usual way to install on one device on request is a group per app: the app is assigned as **Required** to that group, and adding a device to the group installs the app on it at the device's next check-in.
- Microsoft Graph calls, with an Entra ID app registration and the client-credentials grant (`https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token`, scope `https://graph.microsoft.com/.default`):
  - List apps for the editor's picker: `GET /v1.0/deviceAppManagement/mobileApps?$filter=...&$select=id,displayName,publisher`. Windows apps only: Win32, winget (`#microsoft.graph.winGetApp`), MSI line-of-business, and Store apps.
  - The device: `GET /v1.0/deviceManagement/managedDevices/<intuneDeviceId>` (its `azureADDeviceId`), then the directory object: `GET /v1.0/devices?$filter=deviceId eq '<azureADDeviceId>'&$select=id`.
  - The group: `POST /v1.0/groups` with `securityEnabled: true`, `mailEnabled: false`; members: `POST /v1.0/groups/<id>/members/$ref` and `DELETE /v1.0/groups/<id>/members/<objectId>/$ref`.
  - The assignment: `GET /v1.0/deviceAppManagement/mobileApps/<appId>/assignments`, and `POST .../assignments` to add one. **Not** the `assign` action, which replaces every assignment the app has.
  - Check-in now: `POST /v1.0/deviceManagement/managedDevices/<id>/syncDevice`.
  - The result: `POST /beta/deviceManagement/reports/retrieveDeviceAppInstallationStatusReport` filtered by application and device. The older `deviceStatuses` endpoint was retired in 2023.
- Application permissions: `DeviceManagementApps.ReadWrite.All`, `DeviceManagementManagedDevices.Read.All`, `DeviceManagementManagedDevices.PrivilegedOperations.All` (sync), `Group.ReadWrite.All`, `Device.Read.All`. An administrator grants them once.
- The agent can tell the server which Intune device it is. An Intune-enrolled PC carries its Intune device id as `EntDMID` under `HKLM\SOFTWARE\Microsoft\Enrollments\<guid>` where `ProviderID` is `MS DM Server`. So a device with the agent is linked to Intune without anybody typing an id. A device without the agent can have the id typed on its device page.
- The Action1 client has a fake (`FakeAction1Client`, `Action1:Mode=Fake`) that CI's smoke test and every server test use. Intune gets the same.

## Scope

### In

- **Options** `Intune`: `Mode` (`Off`, the default, `Live` or `Fake`), `TenantId`, `ClientId`, `ClientSecret` (from the environment, filled by `op`), `GroupPrefix` ("App Portal - "). With `Off`, the engine is not registered and nothing about Intune shows.
- **`IIntuneClient`** in `src/AppPortal.Server/Intune/`, with `IntuneClient` (Graph over `HttpClient`, token cached until a minute before it expires, 429 and 503 retried after `Retry-After`) and `FakeIntuneClient` (in memory; an install finishes after two refreshes, an app named "Fail" fails):
  - `SearchAppsAsync(term)`, `GetAppAsync(appId)`
  - `GetDeviceAsync(intuneDeviceId)` → name, `azureADDeviceId`, last check-in
  - `FindDirectoryObjectAsync(azureADDeviceId)` → object id
  - `EnsureGroupAsync(displayName)` → group id
  - `AddMemberAsync(groupId, objectId)`, `RemoveMemberAsync(groupId, objectId)` (a member already there, or already gone, is not an error)
  - `EnsureAssignmentAsync(appId, groupId, intent)`, intent `required` or `uninstall`
  - `SyncDeviceAsync(intuneDeviceId)`
  - `GetInstallStateAsync(appId, intuneDeviceId)` → `installed`, `failed` (with error code and text), `pending`, `notInstalled`, `unknown`
- **Catalog:** `CatalogEntry.Intune` (`IntuneAppRef(string AppId, string DisplayName)`), stored as a `catalog_packages` row with engine `intune`, so no catalog column is added. The web and client editors get "Also offer it through Intune (optional)" with a search over Intune apps, like the Action1 package search.
- **Devices:** a column `intune_device_id` on `devices`, migration 025. The agent reports it on every heartbeat (`AgentHeartbeatRequest` gains `string? IntuneDeviceId`), read from the enrollments key; an administrator can set or clear it on the device page. A reported id replaces a typed one.
- **Table** `intune_groups (app_id, intent, group_id, created_at)`, so each app's two groups are created once and found again.
- **`IntuneInstallEngine`** (`Name` "intune", `DisplayName` "Intune", `Rank` 3, `CanStop` false, `CanRemove` true), registered only when Intune is on. `CanInstall`: the app has an Intune app and the device an Intune device id.
  - Start: find the directory object, ensure the app's install group (`<prefix><app name> - install`) with a Required assignment, add the device, remove it from the uninstall group, sync the device. The reference is `<appId>|<intuneDeviceId>`. The detail reads "Sent to Intune. It installs at the PC's next check-in."
  - Refresh: read the install state and map it: `installed` succeeded, `failed` failed with Intune's error text and code, anything else running. An install that stays pending for 24 hours fails with "Intune did not report this install within a day. Check the device in Intune."
  - Remove: the same with the uninstall group and an Uninstall assignment, and the device taken out of the install group.
- **Settings:** the server default engine may be `intune`, and so may a device's preference and an app's override, through the M9-01 lists.
- **Docs:** `docs/server-setup.md`, an "Intune" section: the app registration, the five permissions, the three settings, the groups the portal creates and that nothing else should edit, and how a device is linked. `docs/how-it-works.md` and `docs/administration.md` name the third engine.

### Out

- Creating or uploading apps into Intune. The portal links to apps an administrator has already made there.
- Enrolling PCs into Intune.
- Per-user assignments (user groups). A request installs on the device.
- iOS, Android and macOS apps.

## Interface

```csharp
public sealed record IntuneAppRef(string AppId, string DisplayName);
public sealed record IntuneApp(string Id, string DisplayName, string Publisher, string Type);
public sealed record IntuneDevice(string Id, string DeviceName, string? AzureAdDeviceId, DateTimeOffset? LastSyncAt);
public sealed record IntuneInstallState(string State, string? ErrorCode, string? Detail);
```

## Steps

1. Tests first, against `FakeIntuneClient` and against `IntuneClient` over a recording `HttpMessageHandler` with Graph responses copied from the Graph documentation: the token request and its cache; each call's URL, method and body; paging with `@odata.nextLink`; 429 with `Retry-After`; the assignment check that never calls `assign`; the engine's start, refresh and remove; the 24-hour limit; the ladder with three engines.
2. Options, clients, registration, migration, stores, engine.
3. Agent: read the Intune device id, with a Windows-only test that reads the enrollments key without failing on a PC that has none.
4. Web and client editors and device pages, the smoke test run with `Intune__Mode=Fake`, docs.

## Acceptance criteria

- In fake mode, an app with only an Intune app installs and is removed on a device with an Intune device id, and the history shows "via Intune".
- The server with `Intune:Mode=Off` behaves exactly as 0.11.0 did.
- Against a real tenant, if one is available: an app linked to an Intune Win32 app installs on an enrolled PC after its next sync, and the install row settles to Succeeded.
- Format, build and all tests pass; the smoke test passes in fake mode.

## Verification

Tests as above. A run against a real tenant needs the owner's app registration; its credentials come from 1Password with `op run`, and the result is recorded in the release pull request.

## Touches

`src/AppPortal.Server/Intune/*` (new), `src/AppPortal.Server/Options/IntuneOptions.cs` (new), `src/AppPortal.Server/Installs/IntuneInstallEngine.cs` (new), `src/AppPortal.Server/Catalog/CatalogStore.cs`, `src/AppPortal.Server/Devices/DeviceStore.cs`, `src/AppPortal.Server/Data/Migrations/025-intune.sql` (new), `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `src/AppPortal.Server/Admin/Api/{AdminCatalogEndpoints,AdminDeviceEndpoints}.cs`, `src/AppPortal.Server/Pages/Admin/{Catalog,Devices}/**`, `src/AppPortal.Server/Program.cs`, `src/AppPortal.Shared/{Contracts,AdminContracts}.cs`, `src/AppPortal.Agent/{HeartbeatClient,HeartbeatWorker}.cs`, `src/AppPortal.Agent/IntuneIdentity.cs` (new), client catalog editor and device page, `deploy/smoke-test.sh`, `docs/*.md`, tests.

## Facts to confirm

1. The request body and column names of `retrieveDeviceAppInstallationStatusReport`, and whether it needs the Intune device id or the Entra device id.
2. Whether a device's directory object can be a member of a group that is used for a Required app assignment, for an Entra-joined and for a hybrid-joined PC.
