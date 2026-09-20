-- Who the install was for. For a machine-wide install this is a record; for a per-user one it decides
-- which profile the software goes into, so the agent needs it on the job rather than having to ask.
-- Denormalised from installs on purpose: the account that asked is what matters, and an install row
-- that is later tidied must not take the meaning of a queued job with it.
ALTER TABLE agent_jobs ADD COLUMN requester TEXT;
