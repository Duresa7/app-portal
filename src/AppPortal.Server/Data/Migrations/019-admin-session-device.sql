-- The Windows client's admin mode signs in from a named PC and holds its token for weeks, so an
-- administrator has to be able to see which machines are signed in as them and cut one off. That needs
-- two things the table did not have: a name for the session, and a handle to revoke it by.

-- What the client called itself when it asked for the token. Null for the browser cookie and for any
-- session created before this migration, because neither ever said.
ALTER TABLE admin_sessions ADD COLUMN device_name TEXT;

-- A handle a session can be named by. The primary key is the SHA-256 of the token, which is derived
-- from the secret and has no business travelling in a URL, so sessions are listed and revoked by this
-- instead. Existing rows get one now; the column is written on insert from here on.
ALTER TABLE admin_sessions ADD COLUMN id TEXT;
UPDATE admin_sessions SET id = lower(hex(randomblob(16))) WHERE id IS NULL;

CREATE UNIQUE INDEX admin_sessions_id ON admin_sessions(id);
