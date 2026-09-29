# M8-01: Portable apps

**Milestone:** 8 (0.11.0)
**Depends on:** M5-05
**Unlocks:** M8-04, M8-06, M8-08

## Goal

A catalog app can be a zip archive that is extracted into a folder, not an installer that is run. The agent downloads it, checks its SHA-256, extracts it for everyone or for one person, adds a Start menu shortcut, and writes an uninstall entry so that Windows, winget and the portal all see it. Removing it takes all of that away again.

## Context

- Many tools ship only as a zip: game mod managers, overclocking and monitoring tools, emulators, command-line utilities, and internal tools. Today the portal can offer them only if somebody builds an installer first.
- `DirectInstallerExecutor` (`src/AppPortal.Agent/Executors/DirectInstallerExecutor.cs`) already downloads a file by URL, resumes it, checks its SHA-256 and caches it, through `ResumableDownload`. `ResumableDownload.FetchAsync` takes a `DirectPackageDefinition` today; it needs only the URL, the hash, the size and a file extension.
- The inventory sweep reads winget's list, and winget lists every uninstall entry under `HKLM\...\Uninstall` and `HKCU\...\Uninstall`. So an uninstall entry is all that a portable app needs to appear in the Installed list and to match its catalog app by name.
- A per-user app goes into the person's profile. SYSTEM cannot expand `%LOCALAPPDATA%` for them, and a folder SYSTEM creates there is owned by SYSTEM. The extraction therefore runs inside the person's session, as a per-user direct install does.

## Scope

### In

- **`PortablePackageDefinition`** in `src/AppPortal.Shared/Packages.cs`, kind `portable`, with the interface below.
- **`PortableAppExecutor`** in `src/AppPortal.Agent/Executors/`, kind `portable`:
  - Downloads through `ResumableDownload`, which gets an overload that takes the URL, SHA-256, size and extension. The direct path calls the new overload and does not change.
  - Writes a PowerShell script for the job to `%ProgramData%\AppPortal\jobs\<job>.portable.ps1` and runs it as SYSTEM for machine scope, or in the requester's session for user scope. Every value in the script is a single-quoted literal, with `'` doubled, and every value has already passed the definition's allowlist.
  - The script expands the archive into a new temporary folder beside the target, removes the old target folder if there is one, renames the new folder into place, creates the shortcut, and writes the uninstall entry. A file that is in use stops it with a clear exit code and message: "{Folder} is open. Close it and install again."
  - Target folder: `%ProgramFiles%\App Portal Apps\<Folder>` for machine scope, `%LOCALAPPDATA%\Programs\App Portal Apps\<Folder>` for user scope.
  - Shortcut, when `ShortcutName` is set: in `%ProgramData%\Microsoft\Windows\Start Menu\Programs` for machine scope, `%APPDATA%\Microsoft\Windows\Start Menu\Programs` for user scope, pointing at `<target>\<Executable>`.
  - Uninstall entry `AppPortalPortable-<Folder>` under `HKLM` (64-bit view) or `HKCU`: `DisplayName` (the shortcut name, or the folder), `DisplayVersion` (the version, when given), `Publisher` "App Portal", `InstallLocation`, `DisplayIcon` (the executable), `NoModify`=1, `NoRepair`=1, and an `UninstallString` and `QuietUninstallString` that run the removal script copied into the target folder as `uninstall-app-portal.ps1`.
  - Uninstall runs the same removal in the same place: folder, shortcut, entry. A folder that is already gone counts as removed.
- **Server:** `CatalogEntry.SourceName` "Portable app (zip)", `Describe`, the web catalog editor (source "Portable app", with URL, SHA-256, size, folder, executable, shortcut name and version fields), and the admin API, which carries the definition as it is.
- **Client:** the same source and fields in `CatalogEditorViewModel` and `CatalogView`, and a demo app.
- **Docs:** the sources table in `docs/administration.md`, and the kinds in `docs/api.md`.

### Out

- Archives other than zip. `.7z` and `.tar.gz` need a tool Windows does not have.
- Adding the folder to `PATH`.
- Updating a portable app in place (M8-05 covers winget updates only). Installing a newer definition over an older one replaces the folder, which is the update.

## Interface

```csharp
public sealed record PortablePackageDefinition(
    string Url,
    string Sha256,
    long SizeBytes,
    string Folder,        // [A-Za-z0-9][A-Za-z0-9 ._-]{0,63}, no trailing dot or space
    string Executable,    // relative path inside the archive to an .exe, e.g. "bin\\tool.exe"; no "..", not rooted
    string Scope = "machine",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ShortcutName = null,  // file name rules as Folder
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version = null,       // [A-Za-z0-9][A-Za-z0-9.+_-]*
    bool RequiresReboot = false) : PackageDefinition
```

- `Kind` is `portable`. `DownloadSizeBytes` returns `SizeBytes`. `PackageDefinition.Kinds` names it.
- The script's exit codes: 0 done; 20 a file is in use; 21 the archive is not a zip or is damaged; 22 the executable is not in the archive. The executor maps each to a sentence.

## Steps

1. Tests first: definition validation (every rule above, including `..`, a rooted path, a non-.exe executable, a quote in any field), JSON round trip with `kind: portable`, and the script text for both scopes (the paths, the quoting, the entry values).
2. The `ResumableDownload` overload, then the executor, with tests over `FakeSessions` and a recording process runner: machine scope runs as SYSTEM, user scope in the session, a missing requester fails, a parked session returns `WaitingForUser`, each exit code gives its sentence.
3. A Windows-only test that runs the real script on a small zip built in the test, as the current user at user scope, checks the folder, the shortcut and the `HKCU` entry, then runs the removal and checks all three are gone.
4. Server and client editors, the sentence, demo data, docs.

## Acceptance criteria

- A portable app installs for everyone and for one person, shows in Settings > Apps and in the portal's Installed list under its catalog app, starts from its Start menu shortcut, and is removed completely.
- A damaged archive, a hash mismatch and a missing executable each fail with a sentence that says which.
- Format, build and all tests pass on Linux and on the Windows leg.

## Verification

`dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test -c Release`. The Windows-only script test runs on the Windows leg of the full gate.

## Touches

`src/AppPortal.Shared/Packages.cs`, `src/AppPortal.Agent/Downloads/ResumableDownload.cs`, `src/AppPortal.Agent/Executors/PortableAppExecutor.cs` (new), `src/AppPortal.Agent/AgentRun.cs`, `src/AppPortal.Server/Catalog/CatalogStore.cs` (the entry's sentence and source name only), `src/AppPortal.Server/Pages/Admin/Catalog/Edit.cshtml*`, `src/AppPortal.Client/ViewModels/Admin/CatalogEditorViewModel.cs`, `src/AppPortal.Client/Views/Admin/CatalogView.axaml`, `src/AppPortal.Client/Services/DemoAdminApiClient.cs`, `docs/administration.md`, `docs/api.md`, tests.
