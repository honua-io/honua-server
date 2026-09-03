-- Additive, nullable lineage columns preserve legacy rows without fabricating identities.
ALTER TABLE honua.feature_change_outbox
    ADD COLUMN IF NOT EXISTS operation_instance_id TEXT,
    ADD COLUMN IF NOT EXISTS correlation_id TEXT,
    ADD COLUMN IF NOT EXISTS audit_id TEXT,
    ADD COLUMN IF NOT EXISTS proposal_id TEXT;

ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS event_id TEXT,
    ADD COLUMN IF NOT EXISTS operation_instance_id TEXT,
    ADD COLUMN IF NOT EXISTS correlation_id TEXT,
    ADD COLUMN IF NOT EXISTS audit_id TEXT,
    ADD COLUMN IF NOT EXISTS proposal_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_feature_changes_event_id
    ON honua.feature_changes(event_id)
    WHERE event_id IS NOT NULL;

ALTER TABLE honua.alert_events
    ADD COLUMN IF NOT EXISTS source_event_id TEXT,
    ADD COLUMN IF NOT EXISTS job_id TEXT,
    ADD COLUMN IF NOT EXISTS operation_instance_id TEXT,
    ADD COLUMN IF NOT EXISTS correlation_id TEXT,
    ADD COLUMN IF NOT EXISTS audit_id TEXT,
    ADD COLUMN IF NOT EXISTS proposal_id TEXT;
