# M5-05: One way to add an app

**Milestone:** 5 (0.7.0)
**Depends on:** M5-01, M5-02
**Unlocks:** M5-06

## Goal

An administrator adds an app by saying where it comes from and naming it there. A person installing it
sees a name, a size and a button. Neither of them meets the shape of the catalog record.

## Context

- The catalog edit page grew one section per thing it learned to do. It now has five cards, an engine
  override, an agent kind selector, and two blocks of fields shown and hidden by a script. M5-01 and
  M5-02 add a Store source and eleven managers to that. Left alone it becomes a form nobody can read.
- The fields an administrator fills in are almost the same for every source: which source, which id,
  which version, machine or per-user. Only the direct installer really differs, because it is the one
  that needs a URL, a hash and a size.
- `_CatalogTable.cshtml` still prints the engines cell as Action1 or none. It has never mentioned the
  agent, so today the catalog list already lies about half the catalog.
- The end user's card is in good shape and mostly needs defending. It shows a name, a publisher, a
  category, a size when known, whether it installs for everyone or for them, and one sentence of
  requirements. None of the new work belongs on it. An administrator's choice of Scoop over
  Chocolatey is not a thing a person installing a text editor should have to read.

## Scope

### In
- One **Source** selector on the catalog edit page, replacing the engine override and the agent kind
  selector as the first thing an administrator chooses: Action1, winget, Microsoft Store, a package
  manager, or a direct download. Picking one shows only the fields that source needs.
- The managers appear as one group in that selector, each with the sentence M5-02 gives it, so an
  administrator reads what Scoop is for rather than guessing.
- The engine override keeps its meaning and stops being a separate control. An app with an Action1
  package and an agent package needs it; an app with one source does not, so it appears only when
  both are filled in.
- Shared fields are written once: id, version, scope, restart, extra arguments. The direct download
  keeps its own fields, and its "Fetch and hash" helper, unchanged.
- The catalog list's engines cell names every source the app has, not just Action1.
- A "what this will do" line under the form, in words: "Installs Google Chrome for everyone on the PC,
  through winget." An administrator reads the sentence instead of reassembling it from six fields.
- The end user's card: no new fields, and a check that a managed or Store app shows exactly what a
  winget app shows today. The one addition is the download size for the sources that know it.
- README: one table of the sources, what each is for, and which are per-user by nature.

### Out
- Any change to the client's own admin screens. M4-04 builds those, after this, against the shape this
  package settles.
- Changing the catalog database. This package is the form over it.
- A wizard, a preview pane, or anything that installs something to find out whether it works.

## Interface

No JSON, database or route changes. The page is the interface, and M4-04 mirrors it.

## Steps

1. The source selector and the field groups, with the page model mapping one source to one definition.
2. The engine override made conditional; a test that an app with both packages can still set it.
3. The engines cell in the list.
4. The sentence under the form, as a pure function over the definition so it can be tested.
5. The card check, and the size for managed and Store apps.
6. README table.

## Acceptance criteria

- Every app shape the catalog can hold is reachable through the source selector, proved by saving one
  of each and re-reading it.
- An app saved before this package loads into the form unchanged and saves back identically.
- The sentence under the form names the app, who it installs for and the source, for all five sources.
- The catalog list names the agent's source for an agent-only app instead of saying none.
- A person's card for a Scoop app and for a winget app differ only where the catalog differs.

## Verification

`dotnet test`; the client rendered in demo mode with one app of each source.

## Touches

`src/AppPortal.Server/Pages/Admin/Catalog/Edit.cshtml*`, `_CatalogTable.cshtml`,
`src/AppPortal.Server/Catalog/CatalogStore.cs`, `src/AppPortal.Client/ViewModels/AppItemViewModel.cs`,
`README.md`, `tests/AppPortal.Server.Tests/CatalogPagesTests.cs`,
`tests/AppPortal.Client.Tests/AppItemViewModelTests.cs`.
