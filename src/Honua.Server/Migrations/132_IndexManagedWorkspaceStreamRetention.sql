-- Server-published stream references outlive the expiring producer job record.
-- Bound reverse retention lookups by immutable object/store identity.
CREATE INDEX IF NOT EXISTS ix_gp_workspace_artifacts_managed_stream
    ON $HonuaSchema$.gp_workspace_artifacts
    ((metadata ->> 'honua.stream.objectKey'), (metadata ->> 'honua.stream.storeReference'), state)
    WHERE metadata ->> 'honua.stream.published' = 'v1';
