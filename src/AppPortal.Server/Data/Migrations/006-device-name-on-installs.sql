-- History outlives the device it happened on. Until now an install only pointed at a device row, and
-- that pointer was a NOT NULL foreign key, so a decommissioned PC could not be removed without taking
-- its history with it; the store refused the removal for exactly that reason.
--
-- Two things change. The device name is copied onto each install, so the record can name the machine
-- with no device row to read it from. And device_id becomes nullable with ON DELETE SET NULL, so the
-- link is kept while the device exists and lets go cleanly when it does not. SQLite cannot alter a
-- constraint in place, so the table is rebuilt; nothing references installs, which makes that safe to
-- do with foreign keys enforced.

CREATE TABLE installs_rebuilt (
    id              TEXT PRIMARY KEY,
    -- Null once the device has been removed. The name below is what the history shows from then on.
    device_id       TEXT REFERENCES devices(id) ON DELETE SET NULL,
    device_name     TEXT NOT NULL DEFAULT '',
    app_id          TEXT NOT NULL,
    app_name        TEXT NOT NULL,
    requested_by    TEXT,
    engine          TEXT NOT NULL DEFAULT 'action1',
    external_ref    TEXT,
    state           TEXT NOT NULL,
    percent         INTEGER NOT NULL DEFAULT 0,
    detail          TEXT,
    requested_at    TEXT NOT NULL,
    completed_at    TEXT,
    last_checked_at TEXT NOT NULL
);

INSERT INTO installs_rebuilt (id, device_id, device_name, app_id, app_name, requested_by, engine,
                              external_ref, state, percent, detail, requested_at, completed_at, last_checked_at)
SELECT i.id, i.device_id, COALESCE((SELECT d.name FROM devices d WHERE d.id = i.device_id), ''),
       i.app_id, i.app_name, i.requested_by, i.engine,
       i.external_ref, i.state, i.percent, i.detail, i.requested_at, i.completed_at, i.last_checked_at
FROM installs i;

DROP TABLE installs;

ALTER TABLE installs_rebuilt RENAME TO installs;

CREATE INDEX installs_device_requested ON installs(device_id, requested_at DESC);
CREATE INDEX installs_requested ON installs(requested_at DESC);
CREATE INDEX installs_device_name ON installs(device_name, requested_at DESC);
