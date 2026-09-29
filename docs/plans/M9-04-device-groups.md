# M9-04: Device groups and per-group catalogs

**Milestone:** 9 (0.12.0)
**Depends on:** M1-06, M1-08, M1-09
**Unlocks:** M9-05

## Goal

An administrator can put devices into groups, such as "Design", "Labs" or "Gaming PCs", and offer a catalog app only to some groups. A PC sees and can install only the apps offered to everyone and the apps offered to one of its groups. An enrollment key can put every PC it enrolls into a group.

## Context

- "Per-group catalogs" is on the Deferred list. Today every device sees every visible app.
- An app can already be hidden (M1-06): it stays in the catalog, is not offered, and can still be installed as another app's prerequisite. An app that is not offered to a device's groups behaves the same way on that device.
- Enrollment keys (M1-08) already carry a default engine that each device enrolled with them gets. A default group works the same way.

## Scope

### In

- **Tables**, migration 026: `device_groups (id, name UNIQUE COLLATE NOCASE, description, created_at)`, `device_group_members (group_id, device_id, PRIMARY KEY both)`, `catalog_app_groups (app_id, group_id, PRIMARY KEY both)`, and `enrollment_keys.group_id` (nullable).
- **Rule:** an app with no groups is offered to every device. An app with groups is offered to a device in at least one of them. `GET /api/v1/catalog` returns only offered apps; an install, update or repair of an app that is not offered is refused as if it were not in the catalog; a prerequisite chain may still include it.
- **`DeviceGroupStore`**: groups (list, create, rename, describe, delete; deleting a group removes its memberships and its app links, and apps left with no groups become offered to everyone, which the page says before it asks), membership, and app links.
- **Enrollment:** a key may name a group; a device enrolled with it joins that group. The key page and the key CLI (`key create --group <name>`) take it.
- **Web admin:** a Groups page (list with device and app counts, create, rename, delete); the device page lists the device's groups with add and remove; the catalog editor has "Offered to: everyone, or these groups"; the catalog list shows a group count.
- **Client admin:** the same, over the admin API: `GET/POST /api/v1/admin/groups`, `PUT/DELETE /api/v1/admin/groups/{id}`, `PUT /api/v1/admin/devices/{id}/groups`, and `AdminCatalogApp` gains `IReadOnlyList<string>? Groups` (group ids).
- **Catalog JSON:** an export carries each app's group names; an import links the groups that exist by name and reports the names it did not find.
- **Docs:** `docs/administration.md`, a "Device groups" section; `docs/api.md`.

### Out

- Groups from a directory or from Entra ID.
- Nested groups.
- Group-based permissions for administrators.

## Interface

```csharp
public sealed record AdminDeviceGroup(string Id, string Name, string? Description, int Devices, int Apps);
public sealed record AdminDeviceGroupUpsert(string Name, string? Description = null);
public sealed record AdminDeviceGroups(IReadOnlyList<string> GroupIds);
```

`AdminDevice` gains `IReadOnlyList<string>? Groups`. `AdminEnrollmentKey`'s create and summary gain `string? GroupId`.

## Steps

1. Tests first: the offer rule (no groups, one group, two groups, a device in none), the catalog endpoint, install refusal, prerequisite chains, deleting a group, enrollment into a key's group, export and import.
2. Migration, store, catalog filter, service checks, enrollment.
3. Admin API, web pages, client pages, CLI, docs.

## Acceptance criteria

- An app offered to "Design" appears on a Design PC and not on any other; a PC moved out of Design stops seeing it on its next refresh.
- A key with a group enrolls PCs straight into it.
- Format, build and all tests pass.

## Touches

`src/AppPortal.Server/Data/Migrations/026-device-groups.sql` (new), `src/AppPortal.Server/Devices/DeviceGroupStore.cs` (new), `src/AppPortal.Server/Catalog/CatalogStore.cs`, `src/AppPortal.Server/Api/PortalEndpoints.cs`, `src/AppPortal.Server/Installs/InstallService.cs`, `src/AppPortal.Server/Enrollment/*.cs`, `src/AppPortal.Server/Cli/{KeyCli,CatalogCli}.cs`, `src/AppPortal.Server/Admin/Api/*.cs`, `src/AppPortal.Server/Pages/Admin/**`, `src/AppPortal.Server/Program.cs`, `src/AppPortal.Shared/AdminContracts.cs`, client admin view models, views and services, `docs/*.md`, tests.
