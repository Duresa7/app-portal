# M4-06: Release 0.7.0

**Milestone:** 4 (0.7.0)
**Depends on:** M4-03, M4-04, M4-05

## Goal

Ship full admin parity in the client through the gate.

## Scope

### In
- README: "Administration" section describes both surfaces and when to use which; screenshots of the client admin pages in light and dark; the API table gains the admin endpoints in summary form with a link to a new `docs/api.md` listing every route, verb, auth scheme and body shape.
- Gate: the Windows client verification renders one admin screenshot in demo mode (`--demo --screenshot a.png 10`) so admin views are built and compose on the runner.
- Bump to 0.7.0, tag after green.

### Out
- Feature work.

## Acceptance criteria

- Tag run green, assets and image published.
- Every admin action in the web UI has a counterpart in the client, checked by the same table used in M4-01's pull request.
- Roadmap rows M4-01 through M4-06 Done; the Deferred list in the roadmap is reviewed and the next milestone drafted.

## Touches

`README.md`, `docs/api.md` (new), `docs/images/*`, `docs/ROADMAP.md`, `.github/workflows/ci.yml`, `Directory.Build.props`.
