# M8-02: Games handed to their launcher

**Milestone:** 8 (0.11.0)
**Depends on:** M3-07, M5-05
**Unlocks:** M8-04, M8-08

## Goal

A catalog app can be a game in Steam, the Epic Games Launcher, GOG Galaxy or Ubisoft Connect. When a person installs it, the agent opens that game's install page in the launcher, in the person's own session, and the person finishes there with their own account. The portal never signs in to a launcher, never drives one, and never downloads game content itself.

## Context

- The roadmap's **Launcher content** decision says the portal installs launchers and applications, and that content a launcher downloads for one account is outside it: "the portal has no account there and no licence to drive one." The owner asked on 2026-09-29 for game software to be covered. A handoff keeps the reason behind the decision: the account and the licence stay with the person and the launcher. The decision changes to say so. It does not change to "the portal installs games".
- Each launcher registers a URI scheme when it is installed. Opening such a URI in the person's session shows the launcher's own install or game page:
  - Steam: `steam://install/<appid>` opens Steam's install dialog. `steam://uninstall/<appid>` opens its uninstall dialog.
  - Epic Games Launcher: `com.epicgames.launcher://apps/<app>?action=launch`. For a game that is not installed, the launcher offers to install it. The protocol activation documentation also names `installer` and `updatecheck`.
  - GOG Galaxy: `goggalaxy://openGameView/<id>`, the game's page with its Install button.
  - Ubisoft Connect: `uplay://launch/<id>/0`, which offers the install for a game that is not installed.
  - Battle.net and the EA app document no install link. They are offered as launchers (M8-04), not as handoff targets.
  - A Microsoft Store or Xbox game needs no handoff: winget's `msstore` source installs it (M5-01).
- `WindowsUserSessions.RunAsAsync` (M3-07) starts a process in a person's session. `rundll32.exe url.dll,FileProtocolHandler <uri>` hands a URI to its registered handler and exits 0. `explorer.exe <uri>` does the same but exits 1, so it is not used.
- A launcher that is not installed for that person has no handler, and Windows then shows "Get an app to open this link". The agent checks for the handler first: `HKCU\Software\Classes\<scheme>` of the person, then `HKLM\SOFTWARE\Classes\<scheme>`, with a `URL Protocol` value.

## Scope

### In

- **`LauncherPackageDefinition`** in `src/AppPortal.Shared/Packages.cs`, kind `launcher`, and a table of launchers, **`GameLaunchers`**, in `src/AppPortal.Shared/GameLaunchers.cs`, with one row per launcher: name, display name, scheme, install URI template, uninstall URI template (Steam only), id rule, and the winget id of the launcher itself.
- **`LauncherHandoffExecutor`** in `src/AppPortal.Agent/Executors/`, kind `launcher`:
  - Refuses a job without a requester.
  - Parks with `WaitingForUser` when the requester is not signed in, as the other per-user executors do.
  - Fails with "{Launcher} is not installed for {account}. Install {Launcher} first." when the scheme has no handler for that person.
  - Otherwise runs `rundll32.exe url.dll,FileProtocolHandler <uri>` in the session. Success reads "Opened in {Launcher}. Finish the install there."
  - Uninstall uses the launcher's uninstall template when it has one; otherwise it fails with "Remove it in {Launcher}."
- **`IProtocolRegistry`** in the agent, with a Windows implementation that reads the person's hive through their SID and the machine hive, and a test double.
- **Server:** the sentence ("Opens {name} in Steam for the person who asks. They finish in Steam with their own account."), the source name, and the web editor (source "Game launcher", a launcher list, a game id field, scope fixed to "Installs for you", and a hint to add the launcher's catalog app to "Requires").
- **Contract:** `CatalogApp` gains `string? HandoffTo = null`, the launcher's display name. The client card then says "Opens in Steam" under the button, and the finished install says "Opened in Steam".
- **Client:** the same source in the catalog editor, the card text, and demo data.
- **Roadmap:** rewrite the **Launcher content** decision as above, and update the README boundary sentence to match.
- **Docs:** `docs/administration.md` (the source row and where to find each launcher's game id) and `docs/api.md`.

### Out

- Signing in to a launcher, reading a launcher's library, or checking that the person owns the game.
- Knowing when the launcher has finished downloading. The game appears in the Installed list after the next sweep, as any software does, because each launcher writes an uninstall entry for its games.
- Battle.net, the EA app, Amazon Games and Rockstar as handoff targets.

## Interface

```csharp
public sealed record LauncherPackageDefinition(
    string Launcher,      // a GameLaunchers name: steam, epic, gog, ubisoft
    string GameId,        // must match the launcher's IdRule
    string Scope = "user",   // must be "user"; a handoff happens in one person's session
    bool RequiresReboot = false) : PackageDefinition;

public sealed record GameLauncher(
    string Name, string DisplayName, string Scheme,
    string InstallUri,            // "{id}" is replaced
    string? UninstallUri,
    Regex IdRule,
    string WingetId);
```

Id rules: Steam `\A[0-9]{1,10}\z`; Epic `\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z`; GOG `\A[0-9]{1,12}\z`; Ubisoft `\A[0-9]{1,10}\z`.

## Steps

1. Tests first: validation per launcher, the URI each builds, JSON round trip, and the executor over `FakeSessions` and a fake protocol registry (handler missing, handler present, not signed in, no requester, uninstall with and without a template).
2. The shared types, the executor and its registration.
3. Server, contract and client.
4. Roadmap decision, README, docs.

## Acceptance criteria

- On a PC where Steam is installed for the person, installing a Steam game from the portal opens Steam's install dialog for that game on their desktop, and the install reads "Opened in Steam".
- Without Steam, the install fails with the sentence above, and nothing opens.
- The game id and launcher cannot put anything but a known URI on a command line.
- Format, build and all tests pass on Linux and on the Windows leg.

## Verification

Tests as above. The release package opens one Steam install dialog by hand on a PC where Steam is installed, if one is at hand.

## Touches

`src/AppPortal.Shared/Packages.cs`, `src/AppPortal.Shared/GameLaunchers.cs` (new), `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Agent/Executors/LauncherHandoffExecutor.cs` (new), `src/AppPortal.Agent/Executors/IProtocolRegistry.cs` (new), `src/AppPortal.Agent/AgentRun.cs`, `src/AppPortal.Server/Catalog/CatalogStore.cs` (sentence, source name, `ToPublic`), `src/AppPortal.Server/Pages/Admin/Catalog/Edit.cshtml*`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`, `src/AppPortal.Client/Views/MainWindow.axaml`, `src/AppPortal.Client/ViewModels/Admin/CatalogEditorViewModel.cs`, `src/AppPortal.Client/Views/Admin/CatalogView.axaml`, `src/AppPortal.Client/Services/Demo*.cs`, `README.md`, `docs/ROADMAP.md`, `docs/administration.md`, `docs/api.md`, tests.

## Facts to confirm

1. Epic: whether `action=launch` or `action=installer` gives the install prompt for a game that is not installed. The executor uses one template, so the fix is one string.
2. Ubisoft: that `uplay://launch/<id>/0` offers the install for a game that is not installed.
