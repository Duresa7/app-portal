-- Hiding an app takes it off the device API without deleting it, so the installs that reference it
-- keep their history. Deleting is still allowed when nothing references the app; the pages offer
-- Hide instead when something does.

ALTER TABLE catalog_apps ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0;

CREATE INDEX catalog_apps_hidden ON catalog_apps(hidden);
