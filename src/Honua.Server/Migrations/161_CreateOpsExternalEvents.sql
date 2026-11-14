CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_external_events (
    event_id TEXT PRIMARY KEY,
    occurred_at TIMESTAMPTZ NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    body JSONB NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_ops_external_events_occurred_at
    ON $HonuaSchema$.ops_external_events (occurred_at DESC, event_id);
CREATE INDEX IF NOT EXISTS ix_ops_external_events_received_at
    ON $HonuaSchema$.ops_external_events (received_at);
