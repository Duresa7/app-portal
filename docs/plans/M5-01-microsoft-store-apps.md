# M5-01: Microsoft Store apps

**Milestone:** 5 (0.7.0)
**Depends on:** M3-03
**Unlocks:** M5-05

## Goal

An administrator can put a Microsoft Store application in the catalog by its Store id, and a person
installs it from the portal like anything else.

## Context

- The Store is not a separate install mechanism. It is a winget **source**: `winget install --source
  msstore --id 9WZDNCRFJ3TJ`. Everything M3-03 built already applies. The locator that finds
  `winget.exe` for SYSTEM, the output parser, the exit-code map, and `winget list` for inventory.
  Adding a whole executor for the Store would duplicate all of it and gain nothing.
- `WingetExecutor.Arguments` never passes `--source` today, so a Store id resolves against
  `winget-pkgs` and fails with "No installer matches this PC".
- A Store id is a product id such as `9WZDNCRFJ3TJ`, not a dotted `Publisher.Name`. The existing id
  rule demands at least one dot, so it rejects every Store id.
- Store applications are MSIX and install into a person's profile. `--scope machine` is refused for
  most of them, and winget answers `0x8A15002B`, which the executor already reports as "No installer
  for <id> matches this PC at machine scope." That message is correct and is the one to leave alone.
- Some Store applications need the person signed in to the Store before a licence is granted. The
  portal has no account there and will not acquire one. M3-08 already gives an administrator a place
  to say so in words the person reads before installing, so that is where this belongs.

## Scope

### In
- `WingetPackageDefinition` gains `Source`, `"winget"` or `"msstore"`, defaulting to `"winget"` so
  every catalog that exists keeps its meaning and its JSON.
- `Arguments` and the uninstall command line pass `--source <source>`. Nothing else in the executor
  changes.
- Validation splits by source: a `winget` source keeps the dotted-id rule; an `msstore` source takes
  `\A[A-Za-z0-9]{12}\z`, which is the Store product id shape, and says so when it does not match.
- `Scope` defaults to `user` for an `msstore` definition, because that is what a Store package is.
  Machine scope stays expressible and stays the administrator's choice; the executor's existing
  message explains a refusal without a new code path.
- Admin catalog page: a Source selector inside the winget section, "winget" or "Microsoft Store". The
  id field's label and placeholder follow the selection, so the form says what it wants.
- The winget lookup helper skips the manifest check for an `msstore` id and says it did, rather than
  reporting a missing manifest as a bad id.
- `deploy/config/catalog.json` gains one commented Store example and `deploy/config/README.md`
  documents the field, including the sign-in caveat and where to find a Store id.

### Out
- Acquiring or driving a Microsoft account. Paid applications. Store-for-Business private catalogues.
- `Add-AppxProvisionedPackage` for a Store id; provisioning needs the package file, which the Store
  does not hand out, and `DirectPackageDefinition` already covers an MSIX an administrator does have.

## Interface

`{"kind":"winget","id":"9WZDNCRFJ3TJ","source":"msstore","scope":"user","version":null,"extraArgs":null,"requiresReboot":false}`

`source` is written after `id` and before `scope`. `PackageDefinitionTests` freezes the property
order, so that test is the definition of this contract and changes in the same commit.

## Steps

1. `Source` on the record, split validation, tests for both sources including a Store id that a
   dotted rule would have rejected and a dotted id that the Store rule would have rejected.
2. `--source` in both command lines; assert the exact argument string for each source.
3. Admin form selector and the label switch; the lookup helper's msstore branch.
4. Frozen-shape test updated, import and export round trip a Store app.
5. Catalog example and documentation.

## Acceptance criteria

- A Store app saved through the form exports, re-imports identically, and reaches a device with
  `installScope == "user"`.
- `Arguments` for an msstore definition contains `--source msstore` exactly once, and for a winget
  definition contains `--source winget`.
- An existing catalog with no `source` field still parses, and its apps still install against winget.
- A dotted id with `source: msstore` is refused on the page with a message naming the Store id shape.

## Verification

`dotnet test`; on the VM, install one free Store application at user scope and confirm it appears
under Installed.

## Touches

`src/AppPortal.Shared/Packages.cs`, `src/AppPortal.Agent/Executors/WingetExecutor.cs`,
`src/AppPortal.Server/Catalog/PackageHelpers.cs`, `Pages/Admin/Catalog/Edit.cshtml*`,
`deploy/config/catalog.json`, `deploy/config/README.md`,
`tests/AppPortal.Server.Tests/PackageDefinitionTests.cs`,
`tests/AppPortal.Agent.Tests/WingetExecutorTests.cs`.
