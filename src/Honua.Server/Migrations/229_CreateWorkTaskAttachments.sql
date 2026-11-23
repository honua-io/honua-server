-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Task completion attachment reservations, claims and retention tombstones (#7432).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.work_task_attachments (
    attachment_id UUID PRIMARY KEY,
    task_id UUID NOT NULL REFERENCES $HonuaSchema$.work_tasks(task_id),
    owner_subject TEXT NOT NULL,
    field_id TEXT NOT NULL,
    filename TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size_bytes BIGINT NOT NULL CHECK (size_bytes > 0),
    blob_key TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL CHECK (status IN ('pending', 'ready', 'claimed', 'expired')),
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    cleanup_after TIMESTAMPTZ NOT NULL DEFAULT now(),
    claimed_by UUID,
    CONSTRAINT work_task_attachments_claim CHECK ((status = 'claimed') = (claimed_by IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS idx_work_task_attachments_cleanup
    ON $HonuaSchema$.work_task_attachments (cleanup_after) WHERE status <> 'claimed';

CREATE OR REPLACE FUNCTION $HonuaSchema$.protect_work_task_attachment()
RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Attachment cleanup retains its upload tombstone' USING ERRCODE = 'restrict_violation';
    END IF;
    IF OLD.status = 'claimed'
        OR (to_jsonb(NEW) - ARRAY['status', 'claimed_by', 'cleanup_after'])
            IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'claimed_by', 'cleanup_after'])
        OR NOT (NEW.status = OLD.status
            OR (OLD.status = 'pending' AND NEW.status IN ('ready', 'expired'))
            OR (OLD.status = 'ready' AND NEW.status IN ('claimed', 'expired'))) THEN
        RAISE EXCEPTION 'Attachment identity and claimed evidence are immutable' USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_work_task_attachments_retention
    BEFORE UPDATE OR DELETE ON $HonuaSchema$.work_task_attachments
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.protect_work_task_attachment();

CREATE OR REPLACE TRIGGER trg_work_task_attachments_no_truncate
    BEFORE TRUNCATE ON $HonuaSchema$.work_task_attachments
    FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.reject_append_only_mutation();
