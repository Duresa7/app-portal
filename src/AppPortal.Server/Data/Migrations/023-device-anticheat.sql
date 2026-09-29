-- The kernel anti-cheat each device's agent found, and what state Windows said each piece was in. For
-- the administrator asked why a game will not start. Replaced whole on every report, like
-- device_managers, so a product somebody removed goes. Nothing reads it to decide anything.
CREATE TABLE device_anticheat (
    device_id  TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    service    TEXT NOT NULL,
    product    TEXT NOT NULL,
    type       TEXT NOT NULL,
    state      TEXT NOT NULL,
    start_type TEXT NOT NULL,
    seen_at    TEXT NOT NULL,
    PRIMARY KEY (device_id, service)
);
