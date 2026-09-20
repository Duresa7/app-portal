-- What an app needs that the portal cannot arrange: Secure Boot, a vendor account, a particular
-- edition of Windows. Plain words rather than a structured form, because the portal never checks
-- these and never refuses an install over one. Installing is not running: software can install
-- perfectly and then decline to start, and predicting that from rules the vendor owns and changes
-- would block people from software that was fine. The person at the PC is better placed to judge,
-- and the portal's job is to make sure nobody can say they were not told.
ALTER TABLE catalog_apps ADD COLUMN requirements TEXT;
