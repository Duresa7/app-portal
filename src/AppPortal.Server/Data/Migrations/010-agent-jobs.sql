CREATE TABLE agent_jobs (
    id              TEXT PRIMARY KEY,
    install_id      TEXT NOT NULL REFERENCES installs(id),
    device_id       TEXT NOT NULL,
    definition_json TEXT NOT NULL,
    state           TEXT NOT NULL,
    attempt         INTEGER NOT NULL DEFAULT 0,
    leased_until    TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
