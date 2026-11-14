-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- The realtime event log (ADR-0085, specification realtime-cross-node-rc3 sections 3 and 5). Every
-- event family except feature changes, which keep honua.feature_changes, is one ordered, replayable
-- sequence. The head row of a family allocates its sequences and stays locked to commit, so commit
-- order equals sequence order. Retention is written here at startup from Realtime:Retention:<family>;
-- neither triggers nor callers pass a TTL.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.realtime_log_heads (
    family    TEXT COLLATE "C" PRIMARY KEY,
    head      BIGINT NOT NULL DEFAULT 0 CHECK (head >= 0),
    retention INTERVAL NOT NULL DEFAULT interval '24 hours' CHECK (retention > interval '0')
);

INSERT INTO $HonuaSchema$.realtime_log_heads (family) VALUES
    ('collab-ops'), ('collab-presence'), ('observations'), ('alerts'), ('jobs'),
    ('catalog'), ('admin'), ('security'), ('mcp')
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.realtime_events (
    family     TEXT COLLATE "C" NOT NULL REFERENCES $HonuaSchema$.realtime_log_heads (family),
    seq        BIGINT NOT NULL,
    scope_key  TEXT COLLATE "C" NOT NULL,
    kind       TEXT NOT NULL,
    payload    JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (family, seq)
);

CREATE INDEX IF NOT EXISTS ix_realtime_events_scope
    ON $HonuaSchema$.realtime_events (family, scope_key, seq);
CREATE INDEX IF NOT EXISTS ix_realtime_events_expires
    ON $HonuaSchema$.realtime_events (expires_at);

-- Appends p_rows, an array of {scope_key, kind, payload}, to one family in one statement and returns
-- the new head. The notification carries no payload: listeners re-read the log. A payload over
-- 64 KiB (as stored text) is a defect in the writer and is refused. Timestamps come from
-- clock_timestamp(), not the transaction start: appends of a family are serialized by its head row,
-- so expires_at never decreases as seq increases while retention is unchanged.
CREATE OR REPLACE FUNCTION $HonuaSchema$.realtime_append(p_family TEXT, p_rows JSONB)
RETURNS BIGINT
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_head BIGINT;
BEGIN
    IF p_rows IS NULL OR jsonb_typeof(p_rows) <> 'array' THEN
        RAISE EXCEPTION 'realtime_append: rows must be a JSON array' USING ERRCODE = '22023';
    END IF;

    IF EXISTS (
        SELECT 1 FROM jsonb_array_elements(p_rows) AS r
        WHERE octet_length((r.value -> 'payload')::text) > 65536) THEN
        RAISE EXCEPTION 'realtime_append: a payload of family % is over 64 KiB', p_family
            USING ERRCODE = '54000';
    END IF;

    IF jsonb_array_length(p_rows) = 0 THEN
        SELECT head INTO v_head FROM $HonuaSchema$.realtime_log_heads WHERE family = p_family;
    ELSE
        WITH h AS (
            UPDATE $HonuaSchema$.realtime_log_heads SET head = head + jsonb_array_length(p_rows)
            WHERE family = p_family RETURNING head, retention, clock_timestamp() AS appended_at),
        appended AS (
            INSERT INTO $HonuaSchema$.realtime_events (family, seq, scope_key, kind, payload, created_at, expires_at)
            SELECT p_family, h.head - jsonb_array_length(p_rows) + r.ordinality, r.value ->> 'scope_key',
                   r.value ->> 'kind', r.value -> 'payload', h.appended_at, h.appended_at + h.retention
            FROM h, jsonb_array_elements(p_rows) WITH ORDINALITY AS r
            RETURNING seq)
        SELECT max(seq) INTO v_head FROM appended;
    END IF;

    IF v_head IS NULL THEN
        RAISE EXCEPTION 'realtime_append: unknown family %', p_family USING ERRCODE = '22023';
    END IF;

    IF jsonb_array_length(p_rows) > 0 THEN
        PERFORM pg_notify('honua_rt_' || p_family, '');
    END IF;

    RETURN v_head;
END;
$$;

COMMENT ON TABLE $HonuaSchema$.realtime_log_heads IS 'One row per realtime event family: the last allocated sequence and the retention written at startup.';
COMMENT ON TABLE $HonuaSchema$.realtime_events IS 'Realtime events by family and sequence, deleted by the shared-state retention sweep after expires_at.';
COMMENT ON FUNCTION $HonuaSchema$.realtime_append(TEXT, JSONB) IS 'Appends a batch of events to one family, notifies honua_rt_<family> without a payload and returns the new head.';
