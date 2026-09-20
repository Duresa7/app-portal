-- Where an administrator's password is checked. 'local' is a PBKDF2 hash in the row below; 'directory'
-- is a bind against a configured domain controller, and the hash column holds a value that can never be
-- matched. Existing accounts are local, which is what the default says.

ALTER TABLE admins ADD COLUMN source TEXT NOT NULL DEFAULT 'local';
