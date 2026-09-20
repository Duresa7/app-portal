-- Requests remain in the administrator's history after a device is retired. The nullable
-- owner keeps that history separate from any replacement device registered with the same name.
CREATE TABLE app_requests_rebuilt (
    id              TEXT PRIMARY KEY,
    device_id       TEXT REFERENCES devices(id) ON DELETE SET NULL,
    device_name     TEXT NOT NULL,
    requested_by    TEXT,
    text            TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'pending',
    reason          TEXT,
    decided_by      TEXT,
    decided_at      TEXT,
    created_at      TEXT NOT NULL
);

INSERT INTO app_requests_rebuilt (id, device_id, device_name, requested_by, text, status,
                                  reason, decided_by, decided_at, created_at)
SELECT r.id, r.device_id, d.name, r.requested_by, r.text, r.status,
       r.reason, r.decided_by, r.decided_at, r.created_at
FROM app_requests r
JOIN devices d ON d.id = r.device_id;

DROP TABLE app_requests;
ALTER TABLE app_requests_rebuilt RENAME TO app_requests;

CREATE INDEX app_requests_status_created ON app_requests(status, created_at DESC);
CREATE INDEX app_requests_device_created ON app_requests(device_id, created_at DESC);
