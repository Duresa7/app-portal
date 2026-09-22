-- Which package managers each device's agent found, so an administrator can see how many PCs could
-- install an app through one before offering it, instead of learning it from a failure on every PC.
-- Account is empty for a manager every account can use, and names the person for one that lives in a
-- profile. Replaced whole on every report, like device_software, so a manager that was removed goes.
CREATE TABLE device_managers (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    manager   TEXT NOT NULL,
    account   TEXT NOT NULL DEFAULT '',
    version   TEXT NOT NULL DEFAULT '',
    seen_at   TEXT NOT NULL,
    PRIMARY KEY (device_id, manager, account)
);
