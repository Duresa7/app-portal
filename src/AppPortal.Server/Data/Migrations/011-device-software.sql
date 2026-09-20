-- What the agent reports is on a device. Action1 keeps its own inventory and the portal reads that
-- through the API; a device with no Action1 endpoint has no other way to say what it carries, and a
-- device with both is merged at read time.
CREATE TABLE device_software (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    name      TEXT NOT NULL,
    version   TEXT NOT NULL DEFAULT '',
    seen_at   TEXT NOT NULL,
    PRIMARY KEY (device_id, name)
);
