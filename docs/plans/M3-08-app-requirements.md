# M3-08: Requirements the person reads before installing

**Milestone:** 3 (0.5.0)
**Depends on:** M3-01
**Unlocks:** M3-06

## Goal

A catalog app can state what it needs in plain words, and the person reads it before they install. The portal shows the requirement. It does not check it.

## Context

- Installing is not the same as running. A kernel-mode anti-cheat driver installs cleanly on a PC with Secure Boot turned off, and then the game will not start. The install was never the thing that failed.
- So a check would not be checking the install. It would be the portal predicting whether software will work later, from rules the vendor owns and changes. To do it the agent would have to read TPM state, Secure Boot state and build numbers into a table on every heartbeat, and the server would refuse installs from that table. That is a lot of machinery pointed at a guess, and every time the guess is wrong it blocks somebody from software that would have been fine.
- The person at the PC is better placed to know, and it is their machine. The portal's job is to make sure nobody can say they were not told.

## Scope

### In
- Migration 015 (012 through 014 were taken while this package was open): `catalog_apps` gains `requirements TEXT`, plain text, at most 500 characters. The length is enforced on the page rather than in the store, so that an import of an older catalog cannot fail on one and nobody's words are silently cut. Empty for most apps.
- Catalog edit page: a Requirements box, with help text saying what belongs there. Examples in the placeholder: "Needs Secure Boot and TPM 2.0 turned on", "Needs a Steam account", "Windows 11 only".
- `CatalogApp` gains `Requirements: string?`.
- Client: an app with requirements shows them on the card, and the install button opens a short confirmation carrying the same words with Install and Cancel. One extra click, only on the apps that need it, so the text is read rather than scrolled past.
- Admin catalog list marks which apps carry requirements, so an administrator can see at a glance which ones say nothing and perhaps should.
- Import and export carry the field, and `catalog verify` checks the length.

### Out
- Any check of the device, and any refusal based on one. Reading TPM state, Secure Boot state, build numbers or anything else into the server. A structured requirement format. Requirements that vary by device.
- The free-space check the agent already makes before a large download stays where it is, in M3-04. That one is about whether the download can finish, not about whether the software will run, and the agent is the only thing that can know it.

## Interface

`catalog_apps.requirements`. `CatalogApp.Requirements: string?`. Export field `requirements`.

## Steps

1. Migration, store and contract; round-trip through import and export under test.
2. Catalog edit box and the list marker.
3. Client card text and the confirmation, including an app with no requirements keeping its single-click install.

## Acceptance criteria

- An app with requirements shows them on the card and asks for a confirmation carrying the same words before it installs.
- An app without requirements installs in one click, exactly as it does today.
- The text survives export and re-import unchanged.
- Nothing about the device is read, stored or checked at any point.

## Verification

`dotnet test`; manual page and client check.

## Touches

`src/AppPortal.Server/Catalog/CatalogStore.cs`, `Cli/CatalogCli.cs`, `Pages/Admin/Catalog/Edit.cshtml*`, `Pages/Admin/Catalog/Index.cshtml*`, `Data/Migrations/012-app-requirements.sql`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`, `Views/*.axaml`, `tests/AppPortal.Server.Tests/CatalogStoreTests.cs`, `tests/AppPortal.Client.Tests/AppItemTests.cs`.
