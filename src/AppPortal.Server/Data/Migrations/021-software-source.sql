-- Which tool found the software: winget, or the package manager that installed it. Each is reported
-- by a separate run, so each report must replace only its own rows; without this column a Scoop list
-- would erase the winget list every time it arrived, and the other way round.
--
-- The primary key widens with it, so the table is rebuilt. Unlike 013 the rows are kept: every one of
-- them came from winget, which is what the default says.
CREATE TABLE device_software_new (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    account   TEXT NOT NULL DEFAULT '',
    source    TEXT NOT NULL DEFAULT 'winget',
    name      TEXT NOT NULL,
    version   TEXT NOT NULL DEFAULT '',
    seen_at   TEXT NOT NULL,
    PRIMARY KEY (device_id, account, source, name)
);

INSERT INTO device_software_new (device_id, account, source, name, version, seen_at)
SELECT device_id, account, 'winget', name, version, seen_at FROM device_software;

DROP TABLE device_software;

ALTER TABLE device_software_new RENAME TO device_software;
