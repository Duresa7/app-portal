-- What the enrollment endpoint (M2-01) needs that the schema does not have yet.
--
-- machine_id is a stable hardware identifier the installer computes, so a PC that is reimaged or has
-- the client reinstalled updates the row it already owns instead of appearing twice under a second
-- name. It is nullable because every device added by hand, and every device from before enrollment
-- existed, has none; SQLite treats NULLs as distinct in a unique index, so any number of rows may
-- carry none while no two may share one. The column is added and indexed separately because SQLite's
-- ALTER TABLE ADD COLUMN does not accept a UNIQUE constraint.
ALTER TABLE devices ADD COLUMN machine_id TEXT;

CREATE UNIQUE INDEX devices_machine_id ON devices(machine_id);

-- Every attempt, accepted or refused, so an administrator reading a key can see what it has let in.
-- key_id is null when the presented key matched no row at all: there is nothing to attribute it to.
-- device_id carries no foreign key on purpose, the same bargain the install history makes, so the
-- record of an enrollment outlives the device it created.
CREATE TABLE enrollment_events (
    id         TEXT PRIMARY KEY,
    key_id     TEXT REFERENCES enrollment_keys(id),
    device_id  TEXT,
    source     TEXT NOT NULL,
    outcome    TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX enrollment_events_key ON enrollment_events(key_id, created_at DESC);
