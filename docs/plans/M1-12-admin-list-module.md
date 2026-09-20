# M1-12: Administration list queries in one module

**Milestone:** 1 (0.3.0, after the release)
**Depends on:** M1-05, M1-06, M1-07, M1-08, M1-09
**Unlocks:** M4-01

## Goal

The six administration list pages ask one module for a slice of a list instead of each hand-rolling paging, filtering, link building and the htmx partial decision, so that M4-01 can serve the same lists over JSON without writing any of it a second time.

## Context

- The install filter is written out five times in four notations: six field names in `Installs/Index.cshtml`, seven `[BindProperty(SupportsGet = true)]` declarations, six constructor arguments building `InstallFilter`, six hand-escaped pairs in `QueryFor`, and once more in `_InstallRows.cshtml` as `Model.QueryFor(Model.Skip).Replace('?', '&')`. Adding one filterable column is a five-file edit.
- Two pages page, by incompatible rules: installs by `Skip` with a `COUNT`, requests by `p` with a `PageSize + 1` lookahead that deliberately avoids the second query. Catalog, devices, keys and admins read whole tables and filter in memory.
- Five page models decide whether htmx asked for the table alone. Four test `Request.Headers.ContainsKey("HX-Request")`; `Devices/Detail` tests `Request.Headers["HX-Request"] == "true"`. Two of the five carry a byte-identical `Done()` method, doc comment included.
- Four different things are handed to a table partial: `CatalogTableView`, `RequestTableView`, a bare collection, and in `Installs/Index` the page model itself, which is why `_InstallRows.cshtml` writes a fully qualified type name inside markup.
- No list anywhere can be sorted without editing SQL in a store.
- M4-01 specifies `/api/v1/admin/` endpoints as "a thin adapter over the existing stores" with `limit`/`offset` paging. They can only be thin if the list query is a module, which is why M4-01 depends on this package.
- The module composes a query; it does not compose SQL. See `docs/adr/0001-list-queries-are-handed-to-the-stores.md`.

## Scope

### In
- `ListQuery`: the slice wanted (`Limit`, `Offset`, `WantTotal`), the sort wanted, and a filter the module treats as opaque. `Slice<T>`: the rows, the offset they start at, whether more follow, and the total when it was asked for.
- `IListFilter`: a filter writes its own fields to a query string and reads them back. `InstallFilter` implements it with six fields; requests with one; catalog and devices with a single search term; keys and admins with none.
- `AdminListPage<TFilter>`: the shared page model the six list pages inherit. Holds the bound `ListQuery`, the `Error`/`Notice` pair, the reload-after-an-edit step, and one implementation of the htmx partial-or-whole-page decision, tested one way.
- Every table partial takes a `Slice<T>`. `CatalogTableView` and `RequestTableView` go; `_InstallRows.cshtml` stops receiving the page model.
- Form controls bind to the filter, so a field name is declared once, in the filter.
- Store list methods take a `ListQuery` and return a `Slice<T>`. Each store declares the columns it will sort by and keeps its current ordering as the default. Existing SQL, transactions and guarantees are otherwise untouched.
- Tests: `ListQuery`/`Slice`/`IListFilter` round-trip and paging tested directly; store list methods tested against a real database; one guard per list page for signed-in 200 and signed-out redirect. The `SignedIn()` helper and the antiforgery-token regex move into `TestDatabase.cs` and stop being copied.
- `CONTEXT.md` (new) and `docs/adr/0001-list-queries-are-handed-to-the-stores.md` (new).

### Out
- Any visible change. The four unpaged lists stay unpaged, no page gains a sort control, no route is added or changed, no rendered markup changes except where a partial's model type does.
- Any JSON endpoint. M4-01 owns those.
- Shared query machinery in `Data/Database`, an ORM, or any change to how a store writes SQL.
- The `Error`/`Notice` duplication on the detail, edit and login pages, and the flash markup copied into six `.cshtml` files.

## Interface

Other packages build against these.

```csharp
public sealed record ListQuery(int? Limit, int Offset, string? Sort, bool WantTotal);
public sealed record Slice<T>(IReadOnlyList<T> Rows, int Offset, bool HasMore, int? Total);

public interface IListFilter
{
    void Write(IDictionary<string, string?> query);
    static abstract TSelf Read(IReadOnlyDictionary<string, string?> query);
}
```

`ListQuery.Limit` of `null` means the whole list, which is what the four unpaged pages ask for. `Sort` of `null` means the store's own default ordering. `Total` is populated only when `WantTotal` is set: the installs page sets it, the requests page does not.

Store list methods take `(TFilter filter, ListQuery query)` and return `Slice<TRow>`.

## Steps

1. `ListQuery`, `Slice<T>`, `IListFilter`, and their tests. No caller yet.
2. `AdminListPage<TFilter>` with the single htmx decision; the six list pages inherit it and keep their current behaviour.
3. Store list methods move to `(filter, query)` one store at a time, each with the sort columns it allows and its existing ordering as the default.
4. Filters implement `IListFilter`; form controls bind to them; `QueryFor` and the `Replace('?', '&')` in `_InstallRows.cshtml` go.
5. Partials take a `Slice<T>`; `CatalogTableView` and `RequestTableView` are deleted.
6. Page tests: the HTML-string assertions move down to module and store tests, six guards remain, the duplicated sign-in helper and token regex move into `TestDatabase.cs`.

## Acceptance criteria

- Every administration list page renders, filters, and pages exactly as it does on `main`, checked by hand against 0.3.0 for all six.
- Adding a filterable column to installs is one field on `InstallFilter` and one form control; nothing else needs editing.
- Asking a store for a slice of 50 at offset 100 returns the same rows as reading the whole list and skipping 100, and reports `HasMore` correctly at the end.
- No page sets `Sort`; setting it in a test changes the order returned, and an unrecognised sort column is refused rather than interpolated.
- `grep -c 'HX-Request'` over `src/` returns 1.

## Verification

`dotnet format --verify-no-changes`; `dotnet test`; `deploy/smoke-test.sh` against a locally built image; a manual pass over the six list pages comparing against 0.3.0.

## Touches

`src/AppPortal.Server/Admin/Lists/*.cs` (new), `Pages/Admin/AdminListPage.cs` (new), `Pages/Admin/{Admins,Catalog/Index,Devices/Index,Installs/Index,Keys/Index,Requests/Index}.cshtml{,.cs}`, `Pages/Admin/Catalog/{CatalogTableView.cs,_CatalogTable.cshtml}`, `Pages/Admin/Installs/_InstallRows.cshtml`, `Pages/Admin/Keys/_KeyTable.cshtml`, `Pages/Admin/Requests/_RequestTable.cshtml`, `Pages/Admin/Shared/_AdminTable.cshtml`, `Catalog/CatalogStore.cs`, `Devices/DeviceStore.cs`, `Installs/InstallStore.cs`, `Enrollment/EnrollmentKeyStore.cs`, `Requests/AppRequestStore.cs`, `Admin/AdminStore.cs`, `tests/AppPortal.Server.Tests/{ListQueryTests.cs (new),TestDatabase.cs,Admin*PageTests.cs,InstallStoreQueryTests.cs}`, `CONTEXT.md` (new), `docs/adr/0001-list-queries-are-handed-to-the-stores.md` (new), `docs/ROADMAP.md`.
