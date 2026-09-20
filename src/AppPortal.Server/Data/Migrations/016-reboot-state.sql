-- Whether this install is finished or only installed. Some software is registered by its installer
-- and not loaded until the PC boots, a driver most of all, so the exit code says success while the
-- thing still will not run. NULL means the question does not arise; 'pending' means it is waiting for
-- a restart; 'confirmed' means the restart happened and the software was still there afterwards.
--
-- The state column keeps its own meaning. An install waiting for a restart is running, not succeeded,
-- so every page and every filter that already reads state keeps working without knowing about this.
ALTER TABLE installs ADD COLUMN reboot_state TEXT;
