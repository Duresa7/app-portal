# M1-10: Release 0.3.0

**Milestone:** 1 (0.3.0)
**Depends on:** M1-04, M1-05, M1-06, M1-07, M1-08, M1-09
**Unlocks:** M4-01

## Goal

Ship milestone 1 through the release gate with documentation that matches, and an upgrade path for the 0.2.x deployment.

## Scope

### In
- README: replace "Write the catalog in catalog.json" and "Register a device" steps with the admin UI flow (create the first admin with `admin add`, sign in, import or write the catalog, add devices), keep the CLI as the scripted alternative. Add an "Administration" section with one screenshot each of Installs, Catalog and Requests in light theme, produced from the fake-mode server. Document the requests feature in the client section. Update the API table with `/api/v1/requests` and the admin session endpoints. Update "How it works" for SQLite and the `X-AppPortal-User` header. Update Limits.
- `docs/images/`: add `requests.png` from the client and the three admin screenshots.
- Smoke test covers: admin add, admin session, catalog edit reflected on the device API, request submitted from the device API and approved through the admin page handler, install history page returns 200.
- Upgrade note: 0.2.x data on the volume is imported on first start; the config volume no longer needs `catalog.json` after import. Test this by hand once against a copy of a real data volume.
- Bump `Directory.Build.props` to 0.3.0. Tag `v0.3.0` after the run on main is green. A human pushes the tag.

### Out
- Any feature work. If a defect is found during release, fix it in its own branch first.

## Steps

1. Documentation and screenshots.
2. Smoke test additions; run locally.
3. Version bump commit on main, watch the run, then tag.
4. After the tag run: check the GitHub release assets, pull `ghcr.io/duresa7/app-portal-server:0.3.0`, and confirm the package is public.

## Acceptance criteria

- The tag run is green end to end and the release page carries the zip and checksums.
- A fresh `docker compose up -d` plus `admin add` reaches a usable admin UI with no other steps.
- Roadmap table rows M1-01 through M1-10 are Done.

## Touches

`README.md`, `docs/images/*`, `docs/ROADMAP.md`, `deploy/smoke-test.sh`, `Directory.Build.props`.
