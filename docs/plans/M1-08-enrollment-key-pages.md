# M1-08: Enrollment key management

**Milestone:** 1 (0.3.0)
**Depends on:** M1-03
**Unlocks:** M1-10, M2-01

## Goal

Admins create, inspect and revoke enrollment keys. The keys are consumed by the enrollment API in M2-01; this package only manages them, so the setup wizard has something to use the day it exists.

## Context

- Token prefixes: enrollment keys are `ape_` followed by 32 random base32 characters; only the SHA-256 is stored, plus the first 8 characters after the prefix for display.
- A key carries a default engine so enrollment can mark the device `has_agent` and/or expect an Action1 endpoint id.

## Scope

### In
- Migration 005: `enrollment_keys` table.
- `/admin/keys`: table with name, prefix, created by, created at, expires, uses / max uses, default engine, status (active, expired, exhausted, revoked). Create form: name, optional expiry, optional max uses, default engine (`action1`, `agent`, `both`). The full key is shown once after creation with a copy button and never again. Revoke with confirmation.
- `EnrollmentKeyStore` with `Create`, `List`, `Revoke`, and `TryConsume(plaintext) -> EnrollmentKey?` that atomically checks expiry, revocation and remaining uses and increments `uses`. M2-01 calls `TryConsume`.
- CLI: `key create --name <n> [--expires <date>] [--max-uses <n>] [--engine <e>]` for scripted setups.

### Out
- The enrollment endpoint itself (M2-01). Per-key device lists (visible in M1-09 through `enrolled_with_key_id`).

## Interface

```sql
enrollment_keys(id TEXT PRIMARY KEY, name TEXT NOT NULL, key_hash TEXT NOT NULL UNIQUE,
  key_prefix TEXT NOT NULL, default_engine TEXT NOT NULL,   -- 'action1' | 'agent' | 'both'
  expires_at TEXT, max_uses INTEGER, uses INTEGER NOT NULL DEFAULT 0,
  revoked_at TEXT, created_by TEXT NOT NULL, created_at TEXT NOT NULL);
```

`ViewData["Nav"] = "keys"`.

## Steps

1. Migration, store, key generation and hashing, tests for `TryConsume` edge cases (expired, exhausted, revoked, concurrent last use).
2. Page and create flow with the show-once panel.
3. CLI command.

## Acceptance criteria

- A created key appears once in plaintext and the stored row holds only its hash and prefix.
- `TryConsume` under 20 parallel calls with `max_uses = 1` succeeds exactly once, proven by a test.

## Verification

`dotnet test`.

## Touches

`src/AppPortal.Server/Enrollment/EnrollmentKeyStore.cs` (new), `Pages/Admin/Keys/*`, `Cli/KeyCli.cs`, `Data/Migrations/005-enrollment-keys.sql`, `tests/AppPortal.Server.Tests/EnrollmentKeyStoreTests.cs`.
