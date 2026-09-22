# M5-06: Release 0.7.0

**Milestone:** 5 (0.7.0)
**Depends on:** M5-03, M5-04, M5-05

## Goal

Ship every way software arrives through the gate.

## Scope

### In
- README: a "Where apps come from" section with the table of sources from M5-05, saying what each is
  for and which install into a profile by nature. The boundary the roadmap already sets stays stated
  plainly: the portal installs launchers and applications, and content a launcher downloads for one
  signed-in account is outside it.
- `deploy/config/catalog.json` carries one worked example per source, the Store and a package manager
  included, with the hidden flag where an example is not meant to be installed.
- `docs/api.md` gains the agent's managers endpoint and the software endpoint's `source` parameter.
- Gate: the Windows job installs one managed package and takes it off again, so the new executor is
  exercised on a real PC before the tag rather than after it.
- Bump to 0.7.0, tag after green.

### Out
- Feature work. Anything milestone 4 owns.

## Acceptance criteria

- Tag run green, assets and image published.
- An administrator can add an app from each of the five sources on a running server, and a device
  installs one of each.
- The roadmap rows M5-01 through M5-06 are Done, and the milestone 3 caveat about VM verification is
  either closed or restated with what is still unproven.
- The catalog from 0.6.0 imports into 0.7.0 unchanged and every app in it still installs.

## Verification

Full gate from the Actions tab on `main` with the Windows box ticked, then the tag.

## Touches

`README.md`, `docs/api.md`, `docs/ROADMAP.md`, `deploy/config/catalog.json`,
`.github/workflows/ci.yml`, `Directory.Build.props`.
