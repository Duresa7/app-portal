-- Free-text requests for software that is not in the catalog. An admin approves or denies each one
-- with a reason in M1-05; nothing here links a request to a catalog app, on purpose.

CREATE TABLE app_requests (
    id              TEXT PRIMARY KEY,
    device_id       TEXT NOT NULL REFERENCES devices(id),
    requested_by    TEXT,
    text            TEXT NOT NULL,
    -- 'pending' | 'approved' | 'denied'
    status          TEXT NOT NULL DEFAULT 'pending',
    reason          TEXT,
    decided_by      TEXT,
    decided_at      TEXT,
    created_at      TEXT NOT NULL
);

CREATE INDEX app_requests_status_created ON app_requests(status, created_at DESC);
CREATE INDEX app_requests_device_created ON app_requests(device_id, created_at DESC);
