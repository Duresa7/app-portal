# M8-04: Catalog packs

**Milestone:** 8 (0.11.0)
**Depends on:** M8-01, M8-02, M8-03
**Unlocks:** M8-08

## Goal

An administrator can add a ready-made set of apps to the catalog in one step, instead of typing each one. The first packs are **Games** (launchers, anti-cheat clients, game runtimes and gaming tools) and **Essentials** (browsers, archivers, editors, meeting apps). Each entry comes with the right scope, restart flag, requirements note and prerequisites, so the hard parts are already done.

## Context

- `catalog import` (M1-06) reads a `catalog.json` and writes every app in it, replacing apps with the same id. A pack must never replace an app an administrator has already set up.
- Game software has sharp edges that an administrator adding it by hand gets wrong: Riot's games install Vanguard, which needs a restart and, on Windows 11, TPM 2.0 and Secure Boot; FACEIT's client installs a kernel driver; older games need the DirectX End-User Runtime, XNA or old Visual C++ runtimes. A pack records these once.
- winget ids checked on 2026-09-29 with `winget show --id <id> --exact`: `Valve.Steam`, `EpicGames.EpicGamesLauncher`, `Blizzard.BattleNet`, `ElectronicArts.EADesktop`, `Ubisoft.Connect`, `GOG.Galaxy`, `Amazon.Games`, `RockstarGames.Launcher`, `Mojang.MinecraftLauncher`, `PrismLauncher.PrismLauncher`, `Playnite.Playnite`, `HeroicGamesLauncher.HeroicGamesLauncher`, `Overwolf.CurseForge`, `RiotGames.Valorant.NA`, `RiotGames.LeagueOfLegends.NA`, `FACEITLTD.FACEITClient`, `Discord.Discord`, `OBSProject.OBSStudio`, `Guru3D.Afterburner`, `Parsec.Parsec`, `LizardByte.Sunshine`, `PlayStation.PSRemotePlay`, `Microsoft.VCRedist.2015+.x64`, `Microsoft.VCRedist.2015+.x86`, `Microsoft.VCRedist.2013.x64`, `Microsoft.VCRedist.2012.x64`, `Microsoft.VCRedist.2010.x64`, `Microsoft.VCRedist.2008.x64`, `Microsoft.DirectX`, `Microsoft.XNARedist`, `Microsoft.DotNet.DesktopRuntime.8`, `Microsoft.DotNet.DesktopRuntime.9`, `Nvidia.PhysX`. The Xbox app comes from the Store as `9MV0B5HZVK9Z`.

## Scope

### In

- **Packs** as embedded resources of the server, `src/AppPortal.Server/Catalog/Packs/<name>.json`, in the `catalog.json` format, each with a `name`, a `title` and a one-line `description` at the top.
  - **Games:** every launcher above, the Xbox app, the two Riot games (restart, and a requirements note naming Vanguard, TPM 2.0 and Secure Boot), FACEIT (restart, and a note that it installs a kernel driver), the runtimes as their own apps under the category "Runtimes", the gaming tools, and three Steam handoff examples (M8-02) that require Steam: Counter-Strike 2 (730), Dota 2 (570) and Team Fortress 2 (440).
  - **Essentials:** 7-Zip, Google Chrome, Mozilla Firefox, Notepad++, VLC, Zoom, Microsoft Teams, Adobe Acrobat Reader, Visual Studio Code, PowerToys. Ids checked the same way before they are written.
- **`CatalogPacks`** in `src/AppPortal.Server/Catalog/`: lists the packs and adds one. Adding writes only the apps whose id is not in the catalog, and their prerequisite links; it returns how many were added and which were skipped because they exist.
- **CLI:** `catalog packs` lists them; `catalog add-pack <name>` adds one.
- **Admin API:** `GET /api/v1/admin/catalog/packs` and `POST /api/v1/admin/catalog/packs/{name}`, returning `AdminCatalogPackResult(int Added, IReadOnlyList<string> Skipped)`.
- **Web admin:** an "Add a pack" section on the catalog page: each pack with its description, how many of its apps are new, and a button.
- **Client admin:** the same on the client's catalog page.
- **Docs:** `docs/administration.md`, a "Catalog packs" section.

### Out

- Packs downloaded from the internet. A pack is part of a release and reviewed like code.
- Keeping pack apps up to date after they are added. They are ordinary catalog apps from then on.

## Interface

```csharp
public sealed record CatalogPackSummary(string Name, string Title, string Description, int Apps, int New);
public sealed record AdminCatalogPackResult(int Added, IReadOnlyList<string> Skipped);
```

## Steps

1. Tests first: every pack parses; every entry validates (`PackageDefinition.Validate` and the catalog rules); every `requires` id is in the same pack or is a known id; adding a pack twice adds nothing the second time; an app the administrator changed is left as it is.
2. The packs, `CatalogPacks`, the CLI, the API.
3. Web and client pages, docs.

## Acceptance criteria

- On an empty server, adding **Games** puts every game app in the catalog with its category, scope, restart flag, requirements and prerequisites, and a device with the agent can install Steam and then hand Counter-Strike 2 to it.
- Adding it again adds nothing.
- Format, build and all tests pass.

## Verification

Tests as above. Before merge, every winget id in both packs is checked again with `winget show --id <id> --exact` on a Windows PC, and the result is recorded in the pull request.

## Touches

`src/AppPortal.Server/Catalog/Packs/*.json` (new), `src/AppPortal.Server/Catalog/CatalogPacks.cs` (new), `src/AppPortal.Server/AppPortal.Server.csproj` (embedded resources), `src/AppPortal.Server/Cli/CatalogCli.cs`, `src/AppPortal.Server/Admin/Api/AdminCatalogEndpoints.cs`, `src/AppPortal.Shared/AdminContracts.cs`, `src/AppPortal.Server/Pages/Admin/Catalog/Index.cshtml*`, `src/AppPortal.Client/Services/AdminApiClient.cs`, `src/AppPortal.Client/Services/DemoAdminApiClient.cs`, `src/AppPortal.Client/ViewModels/Admin/CatalogViewModel.cs`, `src/AppPortal.Client/Views/Admin/CatalogView.axaml`, `src/AppPortal.Server/Program.cs`, `docs/administration.md`, tests.
