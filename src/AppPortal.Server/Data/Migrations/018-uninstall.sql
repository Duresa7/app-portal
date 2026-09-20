-- Taking software off again. Removal matters most for the software that was hardest to put on: a
-- driver that loads at boot is exactly what somebody cannot remove themselves from the Settings app
-- without help, and every removal being a ticket is the thing this product exists to avoid.

-- Whether the person who installed an app may take it off again. Off by default: an administrator
-- publishing something decides that, and the safe answer for anything they have not thought about is
-- that only they can remove it.
ALTER TABLE catalog_apps ADD COLUMN user_removable INTEGER NOT NULL DEFAULT 0;

-- A removal is an ordinary row in the same history, with the same states and the same progress, so
-- every page that already reads installs shows removals without being taught anything.
ALTER TABLE installs ADD COLUMN kind TEXT NOT NULL DEFAULT 'install';
ALTER TABLE agent_jobs ADD COLUMN kind TEXT NOT NULL DEFAULT 'install';
