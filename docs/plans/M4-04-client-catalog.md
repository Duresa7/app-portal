# M4-04: Client admin, catalog

**Milestone:** 4 (0.8.0)
**Depends on:** M4-02
**Unlocks:** M4-06

## Goal

Admins manage the catalog from the client: list, create, edit, hide, delete, import and export, including Action1 and agent package definitions with the same helpers as the web page.

## Context

- Endpoints from M4-01 (`/catalog/*`, Action1 search and verify). Package definition records from M3-01 in `AppPortal.Shared/Packages.cs`.
- Icons stay URLs; the client already caches icons (`IconCache`) for cards and can preview them in the editor.

## Scope

### In
- Catalog page: grid with search, hidden filter, engines column; New, Import, Export buttons (file dialogs).
- Editor: all `catalog_apps` fields, match rule, engine override, Action1 package with Search and Verify, agent package with kind selector and fields, "Fetch and hash" (server-side, via the API), validation messages inline, Save and Cancel, unsaved-changes prompt.
- Hide and Delete with the same refusal rule as the web page (delete refused when history exists; offer Hide).
- Demo admin data.

### Out
- Icon upload. Reordering.

## Interface

None new.

## Steps

1. Editor view model with validation mirrored from the server rules; tests.
2. Views; the editor as a full-page form, not a dialog, because of its size.
3. Import and export through file pickers.

## Acceptance criteria

- An app created in the client appears on user devices on their next refresh and on the web catalog page.
- Round-trip: export from the client, import on the web, no diff apart from key order.

## Verification

`dotnet test`; manual against fake mode; screenshots.

## Touches

`src/AppPortal.Client/ViewModels/Admin/CatalogViewModel.cs`, `ViewModels/Admin/CatalogEditorViewModel.cs`, `Views/Admin/CatalogView.axaml*`, `Views/Admin/CatalogEditorView.axaml*`, `Services/DemoAdminApiClient.cs`, `tests/AppPortal.Client.Tests/CatalogEditorTests.cs`.
