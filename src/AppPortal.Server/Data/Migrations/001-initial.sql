-- The first schema. Later milestones add tables and columns in new numbered files; this one is never edited.

CREATE TABLE catalog_apps (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    publisher       TEXT NOT NULL DEFAULT '',
    description     TEXT NOT NULL DEFAULT '',
    category        TEXT NOT NULL DEFAULT '',
    icon_url        TEXT,
    featured        INTEGER NOT NULL DEFAULT 0,
    -- The "match" object from catalog.json, or NULL to match on the app name.
    match_json      TEXT,
    -- 'action1' | 'agent' | NULL to follow the server preference. Used from M3-05.
    engine_override TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

CREATE TABLE catalog_packages (
    app_id          TEXT NOT NULL REFERENCES catalog_apps(id) ON DELETE CASCADE,
    engine          TEXT NOT NULL,
    -- action1: {"packageId":"...","version":"latest"}
    definition_json TEXT NOT NULL,
    PRIMARY KEY (app_id, engine)
);

CREATE TABLE devices (
    id                   TEXT PRIMARY KEY,
    name                 TEXT NOT NULL UNIQUE,
    token_hash           TEXT NOT NULL UNIQUE,
    enabled              INTEGER NOT NULL DEFAULT 1,
    -- NULL when the device has no Action1 endpoint.
    action1_endpoint_id  TEXT,
    has_agent            INTEGER NOT NULL DEFAULT 0,
    engine_preference    TEXT,
    enrolled_with_key_id TEXT,
    agent_version        TEXT,
    last_seen_at         TEXT,
    created_at           TEXT NOT NULL
);

CREATE TABLE installs (
    id              TEXT PRIMARY KEY,
    device_id       TEXT NOT NULL REFERENCES devices(id),
    app_id          TEXT NOT NULL,
    app_name        TEXT NOT NULL,
    -- Filled by M1-02.
    requested_by    TEXT,
    engine          TEXT NOT NULL DEFAULT 'action1',
    -- Action1 automation id, agent job id.
    external_ref    TEXT,
    state           TEXT NOT NULL,
    percent         INTEGER NOT NULL DEFAULT 0,
    detail          TEXT,
    requested_at    TEXT NOT NULL,
    completed_at    TEXT,
    last_checked_at TEXT NOT NULL
);

CREATE INDEX installs_device_requested ON installs(device_id, requested_at DESC);
CREATE INDEX installs_requested ON installs(requested_at DESC);
