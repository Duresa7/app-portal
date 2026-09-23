# M6-02: From a request to an app

**Milestone:** 6 (0.9.0)
**Depends on:** None
**Unlocks:** M6-04

## Goal

When an administrator approves a request, they can answer it with an app: one the catalog already holds, or a new one created on the spot, with the create form opened and prefilled from the request. The request keeps a link to the app. On its next refresh the requester's client says the app has been added and takes them to it. Approving still installs nothing.

## Context

- Today an approval is only a decision. `app_requests` (migrations 003 and 007) holds the text, device, requester, status, reason and who decided. The comment in 003 says that nothing links a request to a catalog app, on purpose. The Decisions table in `docs/ROADMAP.md` says the same ("No link from a request to a catalog app"), and so do the README and the approve section of `docs/api.md`. The owner has decided to change this, so the Decisions row changes in this package, first, as the roadmap asks. Migration 003 is never edited; the new migration says that it supersedes the remark.
- `AppRequestStore.Decide` writes only to a pending row, which is how a second administrator is refused with 409. Nothing ever deletes a request, and nothing moves one out of `approved`. Once approved, a request stays approved, so a link written after the approval cannot race with a status change.
- The admin JSON API is a thin adapter over the same store methods the pages call (`AdminApi.cs`). The pages keep calling the stores directly. New behaviour goes into the store once and is exposed through both doors, as M4-01 requires.
- `PUT /api/v1/admin/catalog/{id}` creates or replaces. The client editor checks `ExistsAsync` before a create, so that a taken id is refused as the web form refuses it. The web create form is `/admin/catalog/new` (`EditModel`, `NewId = "new"`). Its helper buttons (`LookupWinget`, `FetchAndHash`) post to `?handler=...`, which drops the page's own query string, so anything the form must carry across those posts has to be a hidden field.
- The device catalog (`GET /api/v1/catalog`) holds only visible apps that some engine on that device can install. A linked app may be missing from one PC's catalog even when it exists and is visible. Only the client can tell.
- This package adds a column to `app_requests` and none to `catalog_apps`. `CatalogStore.Upsert` and `CatalogStore.Import`, the two catalog writers that must be edited together whenever a catalog column is added, stay untouched.
- The request text comes from any signed-in user on any enrolled PC. It is shown on admin pages and in the create form. Razor's encoding is the only thing between that text and the page: no `Html.Raw` anywhere in this package.
- In `AdminScriptedApi` (client tests), calls are dispatched by method name and arguments are asserted by position. A changed signature means the existing `AdminRequestsTests` and `AdminApiClientTests` rows are updated with it.

## Scope

### In

- **Migration 022:** a nullable `catalog_app_id` on `app_requests`. No foreign key; the reason is in the SQL comment.
- **Store:** `Decide` can record the app with the approval in one write. A new `Link` sets, changes or clears the app on an approved request. Reads join the catalog, so every record knows the linked app's current name and whether it is hidden or gone.
- **Contracts:** optional trailing fields on `AppRequest`, `AdminRequest` and `AdminDecision`, plus one new body record, `AdminRequestLink`. Older clients ignore the new fields; newer clients read their absence as "no link".
- **Suggested name and id:** one pure function in `AppPortal.Shared` (`RequestSuggestion`), so the web form and the client editor suggest the same thing from the same text. Only the name and id are prefilled. Everything else in a free-text request is a guess, so the full text is shown above the form instead, marked as the source of the suggestion.
- **Web admin, Requests page:**
  - Pending rows get a third button, **Approve and add to the catalog**. It records the approval and then opens `/admin/catalog/new?fromRequest=<id>`.
  - Pending rows also get an optional **Already in the catalog** field (an app id, with a datalist of catalog apps) that the Approve button records with the approval.
  - Approved rows show the linked app. They offer **Add to the catalog** when no app is linked, and a small form to link a different app or remove the link.
- **Web admin, catalog create form:** with `fromRequest` naming an approved request, the form opens with the suggested name and id and a banner quoting the request. Saving links the request to the new app.
- **Client admin, full parity:**
  - The decision dialog offers a catalog app choice when approving, and an **Approve and add to the catalog** button that approves, switches to the Catalog page and opens the editor prefilled.
  - Approved rows show the link and offer **Add to the catalog** and **Catalog app...** (link, change or clear).
  - The editor links the request after a successful save.
- **End-user client:** a linked request says "Added to the catalog as Slack." with a **Show in Apps** button. The button opens Apps with the search set to that app, and the person installs from the card as usual, requirements confirmation included. A linked app this PC is not offered says so, with no button.
- **Linked app hidden or deleted:** the requester sees a plain approval with its reason, as before this package. The administrator sees "(hidden)" or "no longer in the catalog". Deleting an app is never refused because a request names it.
- **Demo data:** the approved Notepad++ request in both demo clients is linked to `notepadpp`.
- **Docs:** `docs/api.md`, README, and the ROADMAP Decisions row.

### Out

- Installing anything on approval, or on the requester's behalf.
- Email or any notification beyond the client's existing refresh.
- Guessing a package source, id, publisher or description from the request text. No winget or Action1 search is run for the administrator.
- Detecting that a request duplicates an existing app, beyond the existing "An app with id 'x' already exists." refusal on create.
- Linking more than one app to a request, or linking a pending or denied request.
- Showing requests on the catalog edit page or the device detail page. `AdminDeviceDetail.RecentRequests` carries the new fields for free, and nothing renders them.
- Re-shooting README images. That is the release package's job.

## Interface

### Database

`src/AppPortal.Server/Data/Migrations/022-request-catalog-app.sql`:

```sql
-- An approved request may name the catalog app that answers it (M6-02). This supersedes the remark
-- in 003 that nothing links a request to a catalog app; 003 itself is never edited.
--
-- NULL for every request decided before this, for every denial, and for an approval nobody linked.
-- No foreign key, on purpose: deleting an app must never be refused because somebody once asked for
-- it, and ON DELETE SET NULL would also fire if a later migration rebuilt catalog_apps, unlinking
-- every request without a word. The store joins on read instead: a link to an app that is no longer
-- there reads as no link to the requester and as "no longer in the catalog" to an administrator.
ALTER TABLE app_requests ADD COLUMN catalog_app_id TEXT;
```

The store always writes the catalog row's own id (`catalog_apps.id` as stored, whatever case the caller used), so the read joins on plain equality.

### Store (`src/AppPortal.Server/Requests/AppRequestStore.cs`)

```csharp
public sealed class AppRequestRecord
{
    // existing members unchanged, plus:
    public string? CatalogAppId { get; set; }     // as stored, even when the app is gone
    public string? CatalogAppName { get; set; }   // null when no app has that id any more
    public bool CatalogAppHidden { get; set; }

    /// The device sees the link only while the app is there to be installed.
    public AppRequest ToPublic();                 // CatalogAppId/Name set only when Status is Approved,
                                                  // CatalogAppName is not null and CatalogAppHidden is false
}

public enum RequestLinkResult { Linked, NoSuchRequest, NotApproved, NoSuchApp }

// Existing signature gains an optional last parameter. catalogAppId must already be the catalog's
// own id (callers resolve it with CatalogStore.Find first). ArgumentException when it is given with
// AppRequestStatus.Denied.
public bool Decide(string id, AppRequestStatus status, string? reason, string decidedBy, string? catalogAppId = null);

// Sets, replaces or (null/blank) clears the app on an approved request. Checks in this order:
// the request exists, it is approved, the app exists (case-insensitive lookup, canonical id stored).
public RequestLinkResult Link(string id, string? catalogAppId);
```

`Select` becomes:

```sql
SELECT r.id, COALESCE(d.name, r.device_name), r.requested_by, r.text, r.status, r.reason,
       r.decided_by, r.decided_at, r.created_at, r.catalog_app_id, c.name, c.hidden
FROM app_requests r
LEFT JOIN devices d ON d.id = r.device_id
LEFT JOIN catalog_apps c ON c.id = r.catalog_app_id
```

`Count` and the sort columns are unchanged.

### Shared contracts

In `src/AppPortal.Shared/Contracts.cs`:

```csharp
public sealed record AppRequest(
    string Id, string Text, string DeviceName, string? RequestedBy, AppRequestStatus Status,
    string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt,
    string? CatalogAppId = null,      // the app that answers this request, when it is in the catalog and visible
    string? CatalogAppName = null);
```

In `src/AppPortal.Shared/AdminContracts.cs`:

```csharp
public sealed record AdminRequest(
    string Id, string Text, string DeviceName, string? RequestedBy, AppRequestStatus Status,
    string? Reason, string? DecidedBy, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt,
    string? CatalogAppId = null,      // as linked, even if the app has since been deleted
    string? CatalogAppName = null,    // null when the linked app is no longer in the catalog
    bool CatalogAppHidden = false);

public sealed record AdminDecision(string? Reason = null, string? CatalogAppId = null);

/// Naming, changing or (null or blank) removing the catalog app an approved request is answered by.
public sealed record AdminRequestLink(string? CatalogAppId);
```

New file `src/AppPortal.Shared/RequestSuggestion.cs`:

```csharp
public static class RequestSuggestion
{
    public const int MaxNameLength = 80;
    public const int MaxIdLength = 64;
    public static string Name(string? requestText);
    public static string Id(string? name);
}
```

`Name`, in order:

1. Take the first line.
2. Cut at the first `,` `;` `:` `(` `!` `?`, at ` - `, or at a `.` followed by whitespace or the end of the line.
3. Trim whitespace.
4. If the result is longer than 80 characters, cut it at the last space at or before 80, or at 80 when there is no space.

`Id`, in order:

1. Normalize to FormD and drop non-spacing marks.
2. Lower-case (invariant).
3. Keep ASCII letters and digits, and turn every run of anything else into one `-`.
4. Trim `-`, cut to 64 characters, and trim `-` again.
5. If the result is empty, `new` or `export`, return an empty string.

| Request text | Name | Id |
|---|---|---|
| `Slack, for the new support rota` | `Slack` | `slack` |
| `Notepad++` | `Notepad++` | `notepad` |
| `Node.js please` | `Node.js please` | `node-js-please` |
| `Blender. For the product renders` | `Blender` | `blender` |
| `7-Zip` | `7-Zip` | `7-zip` |
| `Café Studio (for menus)` | `Café Studio` | `cafe-studio` |
| `new` | `new` | (empty) |
| two lines, `Figma` then `for design` | `Figma` | `figma` |

### Admin JSON API (`src/AppPortal.Server/Admin/Api/AdminRequestEndpoints.cs`)

`POST /api/v1/admin/requests/{id}/approve`: body `AdminDecision`, which may be left out. `catalogAppId` is optional. When it is present and not blank, the approval and the link are recorded in one write.

| Status | When |
|---|---|
| 200 | `AdminRequest`, approved, with `catalogAppId` and `catalogAppName` when an app was named. |
| 400 | The reason is longer than 500 characters after trimming. |
| 404 | No such request. |
| 409 | The request had already been decided. The earlier decision stands. |
| 422 | `catalogAppId` names no app in the catalog. Nothing is decided. |

Checks run in that order: reason, request, app, decision.

`POST /api/v1/admin/requests/{id}/deny`: unchanged, except that a non-blank `catalogAppId` gets 400 "A denied request cannot name a catalog app." and nothing is decided.

`PUT /api/v1/admin/requests/{id}/catalog-app` (new): body `AdminRequestLink`, `{ "catalogAppId": "slack" }`. A null or blank `catalogAppId` removes the link. The body is required.

| Status | When |
|---|---|
| 200 | `AdminRequest` as it stands after the change. |
| 400 | No body. |
| 404 | No such request. |
| 409 | The request is pending or denied. Only an approved request can name a catalog app. |
| 422 | `catalogAppId` names no app in the catalog. |

`Project` fills the three new `AdminRequest` fields from the record. The device route `GET /api/v1/requests` is unchanged apart from what `ToPublic()` now carries.

### Web pages

`Pages/Admin/Requests/Index.cshtml.cs` (`IndexModel` gains a `CatalogStore catalog` constructor parameter):

- `OnPostApprove(string id, string? reason, string? catalogAppId)`: a blank `catalogAppId` behaves exactly as today. An unknown one sets `Error = "No app with id '<x>' is in the catalog. Nothing was decided."`.
- `OnPostApproveAndAdd(string id, string? reason)` (new): decides Approved. On success it redirects to `/admin/catalog/new?fromRequest=<id>` (escaped). On failure it takes the same `Error` path as `Decide` and returns `Done()`.
- `OnPostLink(string id, string? catalogAppId)` and `OnPostUnlink(string id)` (new): call `requests.Link` and map `RequestLinkResult` to `Notice` or `Error`.
  - Notices: "Linked to <name>." and "The request no longer names a catalog app."
  - Errors: "No such request.", "Only an approved request can name a catalog app.", and "No app with id '<x>' is in the catalog."
- `CatalogApps` (`IReadOnlyList<(string Id, string Name)>`), read from `catalog.Entries` in `Load()`. It feeds one `<datalist id="catalog-app-ids">` in `Index.cshtml`, outside the swapped table.

`Pages/Admin/Shared/_RequestRow.cshtml`:

- **Pending rows.** Inside the existing `<details>`:
  - Add an input `catalogAppId` with `list="catalog-app-ids"`, labelled "Already in the catalog? Its id (optional, for Approve)".
  - Add a third button, "Approve and add to the catalog", with `formaction="/admin/requests?handler=ApproveAndAdd"` and **no** `hx-post`, so that it navigates instead of swapping the table.
  - Approve keeps its `hx-post` and `hx-include="closest form"`, so the new field is sent with it.
- **Decision cell of approved rows.** One line, according to the link's state:
  - "Catalog app: <a href="/admin/catalog/{id}">Name</a>"
  - "Catalog app: Name (hidden, so devices are not offered it)"
  - "Catalog app: 'id', no longer in the catalog"
- **Actions cell of approved rows.**
  - When no app is linked, an `<a class="button secondary" href="/admin/catalog/new?fromRequest=<id>">Add to the catalog</a>`.
  - A `<details><summary>Catalog app</summary>` form with the `catalogAppId` input (datalist, current id as its value), **Link** (`?handler=Link`), and **Remove link** (`?handler=Unlink`, shown only when linked). Both buttons use the same htmx and `formaction` pairing as Approve and Deny.

`Pages/Admin/Catalog/Edit.cshtml.cs` (`EditModel` gains an `AppRequestStore requests` constructor parameter before the optional `helpers`):

- `[BindProperty(SupportsGet = true)] public string? FromRequest { get; set; }`. It is not called `Request`, which would hide `PageModel.Request`.
- `public AppRequestRecord? SourceRequest { get; private set; }` is loaded in every handler that returns `Page()`: `OnGet`, the error paths of `OnPost`, `OnPostFetchAndHashAsync` and `OnPostLookupWingetAsync`. It is only loaded when `IsNew` is true and `FromRequest` names an approved request.
  - If `FromRequest` names no request, or a pending or denied one, `FromRequest` is cleared and `Error` says "That request is not approved, so this app will not be linked to it. Approve it on the Requests page first."
  - For an existing app, `FromRequest` is ignored.
- `OnGet` with a loaded `SourceRequest` sets `Name = RequestSuggestion.Name(text)` and `Id = RequestSuggestion.Id(Name)`, then works out `Sentence` from that.
- On a successful `OnPost` with `IsNew` and `FromRequest`, the page calls `requests.Link(FromRequest, entry.Id)` after `catalog.Upsert`. It redirects to `Edit` with `saved = true` and `linked = "yes"`, or `linked = "no"` if `Link` did not return `Linked`.
  - `[BindProperty(SupportsGet = true)] public string? Linked`.
  - yes: "Saved and linked to the request. The person who asked is offered it on their next refresh."
  - no: "Saved, but the request was not linked. Link it from the Requests page."
- `Edit.cshtml`, when `SourceRequest` is set:
  - It renders a banner card above the App card: "For the request from <requester> on <device>: <text>. The name and id are suggested from it, so check both. Saving links the request to this app."
  - It renders `<input type="hidden" name="FromRequest">` inside `#catalog-form`.

### Client admin

`Services/AdminApiClient.cs` (`IAdminApiClient`, `AdminApiClient`, `DemoAdminApiClient`):

```csharp
Task<AdminRequest> ApproveRequestAsync(string id, string? reason, string? catalogAppId, CancellationToken ct); // was (id, reason, ct)
Task<AdminRequest> LinkRequestAsync(string id, string? catalogAppId, CancellationToken ct);                   // PUT .../catalog-app
```

The demo client fills `CatalogAppName` and `CatalogAppHidden` from its own catalog each time it answers `GetRequestsAsync`, as the server's join does. It answers an unknown app with `PortalApiException(..., HttpStatusCode.UnprocessableEntity)` and a non-approved link with `Conflict`.

`ViewModels/Admin/RequestsViewModel.cs`:

- Constructor `RequestsViewModel(IAdminApiClient api, Action<AdminRequest>? addToCatalog = null)`.
- Choices: a new record `CatalogAppChoice(string? Id, string Label)`. The first choice is `(null, "Not in the catalog yet")`, and a hidden app's label ends " (hidden)".
- Page members:
  - `ObservableCollection<CatalogAppChoice> CatalogChoices` and `[ObservableProperty] CatalogAppChoice? SelectedCatalogApp`. The choices are loaded, every page, the way `CatalogViewModel.LoadCoreAsync` loads them, when the decision dialog opens for an approval or the link dialog opens. A failed load leaves only the first choice and sets `ErrorMessage`.
  - `ConfirmDecisionCommand` sends `SelectedCatalogApp?.Id` when approving and null when denying.
  - `ApproveAndAddCommand` (new) runs the same optimistic approval as `ConfirmDecisionCommand`, without an app. Only after the server accepts it does it call `addToCatalog(approvedRequest)`. It is shown only while `IsApproving`.
  - `[ObservableProperty] RequestRowViewModel? Linking`, `IsLinkOpen`, `ConfirmLinkCommand` (sends the chosen id, or null for "Not in the catalog yet", which clears the link), and `CancelLinkCommand`.
- `RequestRowViewModel` members:
  - `CatalogAppText`: "" when there is no link. Otherwise "Catalog app: Name", "Catalog app: Name (hidden)", or "Catalog app: 'id', no longer in the catalog".
  - `HasCatalogApp`, `CanAddToCatalog` (approved, no link, not saving), `CanLink` (approved, not saving), `AddToCatalogCommand` (calls `addToCatalog(Request)`) and `LinkCommand`.
  - The constructor gains the two callbacks.

`ViewModels/Admin/AdminAreaViewModel.cs` wires `Requests = new RequestsViewModel(api, r => { Catalog.NewFromRequest(r); navigate(AdminSections.Catalog); })`.

`ViewModels/Admin/CatalogViewModel.cs`: `public void NewFromRequest(AdminRequest request)` clears `Notice` and `ErrorMessage` and sets `Editor = new CatalogEditorViewModel(Api, null, OnSaved, CloseEditor, request)`. When the list has never loaded, it starts `LoadAsync()`, because `ActivateAsync` skips the load while editing. `OnSaved` reads `Editor.FromRequest` and `Editor.LinkError` before closing the editor:

- Linked: "Saved Slack and linked it to the request from CONTOSO\alee. Devices pick this up on their next refresh."
- Not linked: `Notice` "Saved Slack." and `ErrorMessage` "The request could not be linked. <reason>"

`ViewModels/Admin/CatalogEditorViewModel.cs`:

- The constructor gains an optional last parameter, `AdminRequest? fromRequest = null`. When `app` is null and `fromRequest` is set, the constructor prefills `Name` and `Id` through `RequestSuggestion` while `_loading` is still true, before `_baseline` is taken, so Cancel on an untouched prefilled form closes without asking.
- New members: `AdminRequest? FromRequest`, `bool HasRequest`, `string RequestBanner` (the web banner's words), and `string? LinkError { get; private set; }`.
- `SaveAsync`: after `SaveCatalogAppAsync` succeeds and `FromRequest` is set, it calls `LinkRequestAsync(FromRequest.Id, stored.Id)`. A `PortalApiException` goes into `LinkError`. `_saved(stored)` is called either way, because the app is saved.

`Views/Admin/RequestsView.axaml`:

- The decision popup gains a ComboBox over `CatalogChoices` and an "Approve and add to the catalog" button, both visible while `IsApproving`.
- A second popup, `LinkPopup`, uses the same layout: a ComboBox plus Save and Cancel.
- The Decision column shows `CatalogAppText` and the two row buttons.

`Views/Admin/CatalogEditorView.axaml`: an info bar showing `RequestBanner` while `HasRequest`.

### End-user client

`ViewModels/RequestItemViewModel.cs`: constructor `RequestItemViewModel(AppRequest request, AppItemViewModel? app = null, Action<AppItemViewModel>? show = null)`, where `app` is the catalog card with `Request.CatalogAppId`, if this PC is offered it. Members:

- `CatalogAppText`:
  - "" when `CatalogAppId` is null.
  - "Added to the catalog as {app.Name}." when `app` is set.
  - Otherwise "Added to the catalog as {CatalogAppName}, but this PC cannot install it."
- `HasCatalogAppText`, `CanShowApp` (`app` and `show` both set), and `ShowAppCommand`.

`ViewModels/MainViewModel.cs`:

- `RefreshAsync` builds each `RequestItemViewModel` with the matching card from `Apps` (id, OrdinalIgnoreCase). The catalog is merged before requests are read, so the cards are there.
- `private void ShowRequestedApp(AppItemViewModel app)` sets `SelectedCategory = "All"`, `SearchText = app.Name` and `SelectedSection = 0`. It never installs.
- `SubmitRequestAsync` is unchanged.

`Views/MainWindow.axaml`, requests template: under the reason, a caption bound to `CatalogAppText` and a "Show in Apps" button bound to `ShowAppCommand`, visible on `CanShowApp`.

### Roadmap wording

The Requests row of the Decisions table becomes exactly:

> Free-text box in the client. Admins approve or deny with an optional reason. An approval may name the catalog app that answers it, either one already in the catalog or one the administrator creates from the request, and the requester's client then points them to it. The requester sees status and reason in the client. No email. Approving never installs anything.

## Steps

1. **Roadmap first.** Change the Decisions row to the wording above, and set this package's status as the rules say.
2. **Migration and store.** Add migration 022. Extend `AppRequestRecord`, `Select`, `Read`, `ToPublic`, `Decide` and the new `Link`. Write the store tests, and the upgrade test from a database at 021.
3. **Contracts and suggestion.** Add the contract fields and records, and `RequestSuggestion` with its table test.
4. **API.** Extend approve and deny, and add `PUT /requests/{id}/catalog-app` with its API tests. Update `docs/api.md`:
   - the approve and deny text (replace "Approval does not add an app to the catalog or start an install." with "Approval can name an app already in the catalog. It never creates an app or starts an install.");
   - the new route with its status table;
   - the `AppRequest` field table (`catalogAppId`, `catalogAppName`, both string?, only while the app is in the catalog and visible);
   - the `AdminRequest`, `AdminDecision` and `AdminRequestLink` lines.
5. **Web Requests page.** Add the handlers, the datalist, and the pending and approved row changes, with page tests.
6. **Web create form.** Add `FromRequest`, the banner, the hidden field, link-on-save, and the `linked` messages, with page tests.
7. **Client admin.** Change the API client signatures and add `LinkRequestAsync`. Then the demo admin client, `RequestsViewModel`, the `AdminAreaViewModel` wiring, `CatalogViewModel.NewFromRequest`, and the editor prefill and link. Then the views, and the tests.
8. **End-user client.** `RequestItemViewModel`, `MainViewModel`, the requests template, and the demo portal client link on the Notepad++ request, with tests.
9. **README.** Replace the line that says approval never starts an installation with: "Approve or deny with an optional reason of up to 500 characters. An approval can name the catalog app that answers it, or open the create form prefilled from the request; the requester's client then offers that app. Approval never starts an installation." Add `PUT /requests/{id}/catalog-app` to the admin API table. Drop the caveat that no request is linked to a catalog app, or rewrite it to say that approving does not install. Add one sentence to the client Requests paragraph about "Show in Apps".

## Acceptance criteria

- On the web, "Approve and add to the catalog" on a pending request records the approval with its reason and lands on `/admin/catalog/new?fromRequest=<id>` with the suggested name and id and the request text shown. After Save, the requesting device's `GET /api/v1/requests` returns that request with `catalogAppId` and `catalogAppName`.
- The same flow in the client admin ends with the same stored link, proved against the scripted API. The editor opens on the Catalog page prefilled and is not dirty until something is typed.
- Approving with an existing app links in one call. An unknown id gets 422 from the API and an error on the page, and the request stays pending.
- An approved request can be linked, re-linked and unlinked afterwards from both surfaces. A pending or denied request cannot be linked (409, and a page error).
- Hiding the linked app removes the link from the device's view and brings it back when the app is shown again. Deleting the app succeeds, the request stays Approved with its reason, and the admin views show "no longer in the catalog".
- In the end-user client, a linked request whose app is in this PC's catalog shows "Show in Apps". Pressing it opens Apps with that app's card in the filtered list, and nothing is installed. A linked app this PC is not offered shows the "cannot install it" sentence and no button. An unlinked request looks exactly as it did before this package.
- JSON of an `AppRequest` and an `AdminRequest` without the new fields deserializes, with the fields null or false. JSON with them deserializes into records shaped as they were before this package, so older clients keep working.
- A database at migration 021 with approved, denied and pending requests upgrades with every row unchanged and `catalog_app_id` NULL.
- `CatalogStore.Upsert` and `CatalogStore.Import` are untouched, and `catalog_apps` has no new column.
- The ROADMAP Decisions row reads exactly as given under Interface.

## Verification

`dotnet format --verify-no-changes`; `dotnet test`; `deploy/smoke-test.sh` against a locally built image. In fake mode, walk the web flow end to end: approve and add, save, see the device's request list, then hide the app and delete it. Take client screenshots in demo mode, light and dark:

- `AppPortal.exe --demo --screenshot r.png 3` (end-user Requests with the linked Notepad++ request);
- section 13 (admin Requests, with the link on an approved row and the decision dialog open for an approval);
- section 12 (editor opened from a request).

## Touches

`src/AppPortal.Server/Data/Migrations/022-request-catalog-app.sql` (new), `src/AppPortal.Server/Requests/AppRequestStore.cs`, `src/AppPortal.Server/Admin/Api/AdminRequestEndpoints.cs`, `src/AppPortal.Server/Pages/Admin/Requests/Index.cshtml*`, `src/AppPortal.Server/Pages/Admin/Shared/_RequestRow.cshtml`, `src/AppPortal.Server/Pages/Admin/Catalog/Edit.cshtml*`, `src/AppPortal.Shared/Contracts.cs`, `src/AppPortal.Shared/AdminContracts.cs`, `src/AppPortal.Shared/RequestSuggestion.cs` (new), `src/AppPortal.Client/Services/AdminApiClient.cs`, `Services/DemoAdminApiClient.cs`, `Services/DemoPortalApiClient.cs`, `ViewModels/RequestItemViewModel.cs`, `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml`, `ViewModels/Admin/RequestsViewModel.cs`, `ViewModels/Admin/CatalogViewModel.cs`, `ViewModels/Admin/CatalogEditorViewModel.cs`, `ViewModels/Admin/AdminAreaViewModel.cs`, `Views/Admin/RequestsView.axaml`, `Views/Admin/CatalogEditorView.axaml`, `docs/api.md`, `README.md`, `docs/ROADMAP.md`.

Tests:

- `tests/AppPortal.Server.Tests/RequestsApiTests.cs`: `Link` results for each case and canonical id storage; the device sees the link only while the app is visible and present; deleting a linked app is not refused; a denial with an app throws.
- `tests/AppPortal.Server.Tests/RequestHistoryMigrationTests.cs`: 021 to 022 keeps every request, unlinked.
- `tests/AppPortal.Server.Tests/AdminApiTests.cs`: approve with an app, 422, deny with an app returning 400, and `PUT .../catalog-app` returning 200 (set and clear), 404, 409 and 422.
- `tests/AppPortal.Server.Tests/AdminRequestsPageTests.cs`: approve with an app id; ApproveAndAdd redirects and records the approval, and on an already decided request shows the error without redirecting; Link and Unlink; the approved row wording for linked, hidden and deleted apps.
- `tests/AppPortal.Server.Tests/AdminCatalogPageTests.cs`: `fromRequest` prefills and shows the request; save links it; a pending request is refused with the message and saving does not link; Fetch and hash keeps the banner and the hidden field; a taken suggested id is refused and the request stays unlinked.
- `tests/AppPortal.Server.Tests/RequestSuggestionTests.cs` (new): the example table plus the 80- and 64-character cuts.
- `tests/AppPortal.Client.Tests/AdminRequestsTests.cs`: approve sends the chosen app; ApproveAndAdd calls back only after the server accepts; row link text and buttons; the link dialog sends the chosen id or null.
- `tests/AppPortal.Client.Tests/CatalogEditorTests.cs`: prefill without being dirty; link after save with the stored id; a link failure leaves the app saved with `LinkError` set; editing an existing app ignores the request.
- `tests/AppPortal.Client.Tests/AdminShellTests.cs`: approve-and-add lands on Catalog with the editor open.
- `tests/AppPortal.Client.Tests/AdminApiClientTests.cs`: the new route row and the approve body carrying `catalogAppId`.
- `tests/AppPortal.Client.Tests/RequestLinkTests.cs` (new): Show in Apps, the not-offered sentence, and the unlinked row unchanged.
