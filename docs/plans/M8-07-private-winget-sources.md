# M8-07: Private winget sources

**Milestone:** 8 (0.11.0)
**Depends on:** M3-03
**Unlocks:** M8-08

## Goal

An administrator can connect the company's own winget repository to the portal. The portal tells every agent about it, each PC adds it for every account, and a catalog app can come from it like it comes from the public winget source.

## Context

- winget reads REST sources besides its own: `winget source add --name <n> --arg <url> --type Microsoft.Rest`. Companies run one (for example with the winget-cli-restsource reference implementation) for internal and repackaged software.
- A source added with `winget source add` belongs to the account that added it. SYSTEM installs machine-wide packages and each person's session installs per-user ones, so a source added once would serve only one of them.
- The App Installer policy **Enable Windows Package Manager Additional Sources** (`EnableAdditionalSources`) adds sources for every account on the PC, and they cannot be removed by a person. Each source is given in the form that `winget source export` prints: `{"Arg": "<url>", "Data": "<data>", "Identifier": "<id>", "Name": "<name>", "Type": "Microsoft.Rest"}`. The policy is a machine registry key under `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppInstaller`, which SYSTEM can write.
- `WingetPackageDefinition.Source` is `winget` or `msstore` today (M5-01), and the executor always passes it as `--source`.

## Scope

### In

- **Setting:** a list of sources in the settings store, key `winget.sources`, each `WingetSourceConfig(string Name, string Url, string Identifier)`. Name `\A[A-Za-z0-9][A-Za-z0-9._-]{0,31}\z`, not `winget` or `msstore`; URL `https` only, no user info; identifier defaults to the name.
- **Admin:** a "Private winget sources" section on the settings page, web and client, to add, edit and remove sources. `GET/PUT /api/v1/admin/settings/winget-sources`.
- **Catalog:** a winget app may name a configured source. `WingetPackageDefinition.Validate` accepts any name that matches the rule above; the catalog save and import refuse a name that is not configured, with "'{name}' is not a winget source on this server. Add it under Settings first." The editors offer the configured sources in a list after "winget".
- **Agent:** `AgentHeartbeatResponse` gains `IReadOnlyList<WingetSourceConfig>? WingetSources`. `WingetSourcePolicy` in the agent writes the policy when the list changes: `EnableAdditionalSources` = 1 and one value per source under `AdditionalSources`. It writes a marker value `HKLM\SOFTWARE\App Portal\WingetSourcesManaged` = 1, and with an empty list removes only what it wrote before, so a PC whose policy comes from Group Policy or Intune is left alone. After a change it runs `winget source update` as SYSTEM.
- **Docs:** `docs/administration.md` ("Private winget sources", with the note that a Group Policy or Intune setting for the same policy wins), `docs/api.md`.

### Out

- Authenticated sources (winget's `Microsoft.Rest` with Entra ID authentication). They need a token the agent does not have.
- Private feeds for Chocolatey, npm or pip. Their `ExtraArgs` can already name one.

## Interface

```csharp
public sealed record WingetSourceConfig(string Name, string Url, string? Identifier = null);
```

## Steps

1. Tests first: validation of the setting; the catalog refusing an unknown source; the heartbeat carrying the list; `WingetSourcePolicy` over a fake registry (first write, change, removal, a policy it did not write left alone); `WingetExecutor` passing the private source name.
2. Settings store and endpoints, catalog checks, heartbeat.
3. Agent policy writer, and a Windows-only test that writes and removes the policy under a test key.
4. Admin pages, editors, docs.

## Acceptance criteria

- With a source configured, a PC with the agent lists it in `winget source list` for SYSTEM and for a signed-in person within one heartbeat, and a catalog app from it installs for everyone and for one person.
- Removing the source removes it from the PC, unless the policy on that PC was not written by the agent.
- Format, build and all tests pass on Linux and on the Windows leg.

## Touches

`src/AppPortal.Shared/{Packages,Contracts,AdminContracts}.cs`, `src/AppPortal.Server/Settings/SettingsStore.cs`, `src/AppPortal.Server/Agent/AgentEndpoints.cs`, `src/AppPortal.Server/Admin/Api/AdminApi.cs` (settings routes), `src/AppPortal.Server/Catalog/CatalogStore.cs` (source check), `src/AppPortal.Server/Pages/Admin/Settings*`, `src/AppPortal.Server/Pages/Admin/Catalog/Edit.cshtml*`, `src/AppPortal.Agent/{HeartbeatWorker,HeartbeatClient}.cs`, `src/AppPortal.Agent/Executors/WingetSourcePolicy.cs` (new), client settings and catalog editor, `docs/*.md`, tests.

## Facts to confirm

1. The registry form of the policy: the value names under `AdditionalSources` and whether each value holds the whole JSON object, from `DesktopAppInstaller.admx` in the winget-cli repository.
2. Whether a per-user winget session sees a policy source without signing out and in again.
