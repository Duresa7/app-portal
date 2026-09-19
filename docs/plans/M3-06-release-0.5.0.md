# M3-06: Release 0.5.0

**Milestone:** 3 (0.5.0)
**Depends on:** M3-03, M3-04, M3-05

## Goal

Ship the agent install engine through the gate, with documentation that lets a company with no RMM at all set up App Portal end to end.

## Scope

### In
- README: rewrite the opening so Action1 is one option. "How it works" shows both engines. A "Without an RMM" setup path: server, admin, enrollment key with engine `agent`, wizard on a PC, add a winget app, install it. Catalog documentation for agent packages including the games note (size, direct installers, hash). "Limits" updated (winget SYSTEM caveats, no uninstall).
- `deploy/config/catalog.json` gains agent definitions for the existing five apps and two games as examples (Steam via winget, one direct-URL example with a placeholder hash and a comment that it must be filled).
- Smoke test extended for the agent job path (M3-02 added the queue check; add a progress post and completion via curl and assert the install row).
- Screenshots: card with "via Agent", download progress on a large app.
- Bump to 0.5.0, tag after green.

### Out
- Feature work.

## Acceptance criteria

- Tag run green; release assets and image published.
- A tester following only the README on a fresh VM and a fresh server reaches a successful winget install with no Action1 account.
- Roadmap rows M3-01 through M3-06 Done.

## Touches

`README.md`, `deploy/config/catalog.json`, `deploy/config/README.md`, `deploy/smoke-test.sh`, `docs/images/*`, `docs/ROADMAP.md`, `Directory.Build.props`.
