-- An approved request may name the catalog app that answers it (M6-02). This supersedes the remark
-- in 003 that nothing links a request to a catalog app; 003 itself is never edited.
--
-- NULL for every request decided before this, for every denial, and for an approval nobody linked.
-- No foreign key, on purpose: deleting an app must never be refused because somebody once asked for
-- it, and ON DELETE SET NULL would also fire if a later migration rebuilt catalog_apps, unlinking
-- every request without a word. The store joins on read instead: a link to an app that is no longer
-- there reads as no link to the requester and as "no longer in the catalog" to an administrator.
ALTER TABLE app_requests ADD COLUMN catalog_app_id TEXT;
