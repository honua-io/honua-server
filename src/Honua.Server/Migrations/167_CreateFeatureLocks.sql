CREATE TABLE IF NOT EXISTS "$HonuaSchema$".feature_locks (
    service_name TEXT COLLATE "C" NOT NULL,
    layer_id INTEGER NOT NULL,
    feature_id TEXT COLLATE "C" NOT NULL,
    body JSONB NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (service_name, layer_id, feature_id)
);
CREATE INDEX IF NOT EXISTS idx_feature_locks_expires_at ON "$HonuaSchema$".feature_locks (expires_at);
