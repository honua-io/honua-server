CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_incident_observations (
    authority_scope TEXT NOT NULL CHECK (length(btrim(authority_scope)) > 0 AND octet_length(authority_scope) <= 256),
    source_id TEXT NOT NULL CHECK (length(btrim(source_id)) > 0 AND octet_length(source_id) <= 256),
    observation_id TEXT NOT NULL CHECK (length(btrim(observation_id)) > 0 AND octet_length(observation_id) <= 256),
    finding_id TEXT NOT NULL CHECK (length(btrim(finding_id)) > 0 AND octet_length(finding_id) <= 256),
    observed_at TIMESTAMPTZ NOT NULL,
    state INTEGER NOT NULL CHECK (state IN (0, 1, 2)),
    evidence TEXT NOT NULL CHECK (octet_length(evidence) <= 65536 AND evidence::json IS NOT NULL),
    PRIMARY KEY (authority_scope, source_id, observation_id)
);
CREATE INDEX IF NOT EXISTS ix_ops_incident_observations_authority_time
    ON $HonuaSchema$.ops_incident_observations (authority_scope, observed_at DESC, source_id, observation_id);
