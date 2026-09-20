-- Settings the whole server shares. One row per key so a new one needs no migration, which matters
-- because the alternative is a wide table whose columns are mostly NULL and mostly forgotten.
CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- Which engine wins when a device and an app could both be served either way. Action1 is the default
-- because it is the engine that existed first: a fleet upgrading into the agent keeps behaving as it
-- did until somebody decides otherwise.
INSERT INTO settings (key, value) VALUES ('default_engine', 'action1');
