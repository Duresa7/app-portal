-- Keys a machine presents once to enroll itself. The enrollment endpoint that spends them arrives in
-- M2-01; this table exists first so the admin pages and the setup wizard have something to hand out.
-- Only the SHA-256 of a key is stored. The first eight characters are kept in the clear so a row can
-- be told apart in a list without holding the secret that would let anyone enroll with it.

CREATE TABLE enrollment_keys (
    id             TEXT PRIMARY KEY,
    name           TEXT NOT NULL,
    key_hash       TEXT NOT NULL UNIQUE,
    key_prefix     TEXT NOT NULL,
    -- 'action1' | 'agent' | 'both'
    default_engine TEXT NOT NULL,
    expires_at     TEXT,
    max_uses       INTEGER,
    uses           INTEGER NOT NULL DEFAULT 0,
    revoked_at     TEXT,
    created_by     TEXT NOT NULL,
    created_at     TEXT NOT NULL
);

CREATE INDEX enrollment_keys_created ON enrollment_keys(created_at DESC);
