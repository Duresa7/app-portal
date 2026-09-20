-- Who the software belongs to. NULL is machine-wide, everyone on the device. An account name means
-- one profile, and only that person is shown it: what somebody installs for themselves is their own
-- business, and the portal has no reason to publish it to whoever else uses the PC.
--
-- The primary key has to widen with it, and SQLite cannot alter one, so the table is rebuilt. It holds
-- a cache of what the agent last reported, so nothing is lost by starting it empty.
DROP TABLE device_software;

CREATE TABLE device_software (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    account   TEXT NOT NULL DEFAULT '',
    name      TEXT NOT NULL,
    version   TEXT NOT NULL DEFAULT '',
    seen_at   TEXT NOT NULL,
    PRIMARY KEY (device_id, account, name)
);
