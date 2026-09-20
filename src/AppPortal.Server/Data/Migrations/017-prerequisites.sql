-- Software that needs other software first. A game needs its launcher; a great deal of Windows
-- software needs a runtime its own installer does not carry. Without this an administrator publishes
-- three apps and writes "install these first, in this order" in the description, and the portal is
-- back to being instructions rather than a button.
CREATE TABLE catalog_prerequisites (
    app_id          TEXT NOT NULL REFERENCES catalog_apps(id) ON DELETE CASCADE,
    requires_app_id TEXT NOT NULL REFERENCES catalog_apps(id),
    position        INTEGER NOT NULL,
    PRIMARY KEY (app_id, requires_app_id)
);

-- One install, several steps. The history keeps showing one row per thing somebody asked for, which
-- is what they asked for; the steps are how it got there.
CREATE TABLE install_steps (
    install_id   TEXT NOT NULL REFERENCES installs(id),
    position     INTEGER NOT NULL,
    app_id       TEXT NOT NULL,
    app_name     TEXT NOT NULL,
    engine       TEXT NOT NULL,
    external_ref TEXT,
    state        TEXT NOT NULL,
    detail       TEXT,
    PRIMARY KEY (install_id, position)
);
