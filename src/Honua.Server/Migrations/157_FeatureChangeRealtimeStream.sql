-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Feature-change streams read honua.feature_changes directly (ADR-0085, specification
-- realtime-cross-node-rc3 sections 2, 3 and 9). The log row is written in the edit transaction, so
-- an edit made through any task, or directly in SQL, reaches every task's subscribers.

-- One notification per inserting statement wakes every task's tailer; the payload is empty because
-- readers probe the head. Notifications from one transaction are delivered once, at commit.
CREATE OR REPLACE FUNCTION $HonuaSchema$.notify_feature_changes()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_notify('honua_rt_features', '');
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS trigger_notify_feature_changes ON $HonuaSchema$.feature_changes;
CREATE TRIGGER trigger_notify_feature_changes
    AFTER INSERT ON $HonuaSchema$.feature_changes
    FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.notify_feature_changes();

-- Stream readers join each change to its outbox row for the service, protocol and request id.
CREATE INDEX IF NOT EXISTS ix_fco_event_id
    ON $HonuaSchema$.feature_change_outbox (event_id);

-- Durable positions of server-side stream consumers (the webhook dispatcher), as feature-change
-- generations. A consumer with no row starts at the current head.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.stream_cursors (
    name       TEXT COLLATE "C" PRIMARY KEY,
    position   BIGINT NOT NULL CHECK (position >= 0),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON FUNCTION $HonuaSchema$.notify_feature_changes() IS 'Notifies honua_rt_features once per statement that inserts feature changes.';
COMMENT ON TABLE $HonuaSchema$.stream_cursors IS 'The last feature-change generation each server-side stream consumer has processed.';
