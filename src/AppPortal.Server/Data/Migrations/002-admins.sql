-- Local administrator accounts and the sessions they sign in with. Directory-agnostic on purpose:
-- a company running this needs no Active Directory and no identity provider to administer it.

CREATE TABLE admins (
    id              TEXT PRIMARY KEY,
    username        TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password_hash   TEXT NOT NULL,
    disabled        INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL,
    last_login_at   TEXT
);

-- Both the browser cookie and the client's bearer token resolve to a row here, so signing a session
-- out revokes it server-side rather than waiting for a self-contained token to expire.
CREATE TABLE admin_sessions (
    token_hash      TEXT PRIMARY KEY,
    admin_id        TEXT NOT NULL REFERENCES admins(id),
    -- 'web' for the cookie the admin pages use, 'api' for an apa_ bearer token.
    kind            TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    expires_at      TEXT NOT NULL,
    last_used_at    TEXT
);

CREATE INDEX admin_sessions_admin ON admin_sessions(admin_id);
