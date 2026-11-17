-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- This stages an inactive publisher. Readers still use migration 213's MAX and both writer
-- locks remain intact. Activate the prefix only after every supported reader uses the shared
-- horizon function. Feature-change writers must assign their transaction ID before nextval.
-- The sequence must remain monotonic with CACHE 1; never reset it or enable cached allocation.
CREATE TABLE IF NOT EXISTS honua.feature_change_prefix (
    singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
    published BIGINT NOT NULL CHECK (published >= 0),
    pending BIGINT,
    fence XID8,
    CHECK ((pending IS NULL) = (fence IS NULL)),
    CHECK (pending IS NULL OR pending >= published)
);

INSERT INTO honua.feature_change_prefix (singleton, published)
VALUES (TRUE, 0)
ON CONFLICT (singleton) DO NOTHING;

-- One call per fresh READ COMMITTED transaction, outside writers and cursor reads. Publisher
-- instances compete only with each other; a busy instance never blocks a feature writer.
CREATE OR REPLACE FUNCTION honua.publish_feature_change_prefix()
RETURNS BIGINT
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    current_state honua.feature_change_prefix%ROWTYPE;
    allocated BIGINT;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed'
       OR pg_current_xact_id_if_assigned() IS NOT NULL THEN
        RAISE EXCEPTION 'Feature-change publisher requires a fresh READ COMMITTED transaction'
            USING ERRCODE = '25001';
    END IF;
    IF current_setting('transaction_read_only') = 'on' THEN
        RAISE EXCEPTION 'Feature-change publisher requires a writable transaction'
            USING ERRCODE = '25006';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_sequence
        WHERE seqrelid = 'honua.sync_generation'::regclass
          AND seqcache = 1 AND seqincrement = 1 AND NOT seqcycle AND seqmin >= 1
    ) THEN
        RAISE EXCEPTION 'Feature-change publisher requires a monotonic CACHE 1 sequence'
            USING ERRCODE = '55000';
    END IF;
    IF NOT pg_try_advisory_xact_lock(144047712, 2) THEN
        RETURN (SELECT published FROM honua.feature_change_prefix WHERE singleton);
    END IF;

    -- The advisory lock protects this singleton. Avoid FOR UPDATE: it assigns a transaction
    -- ID before reading the sequence and would add writes even on idle cycles.
    SELECT * INTO STRICT current_state FROM honua.feature_change_prefix WHERE singleton;
    IF current_state.pending IS NOT NULL
       AND pg_snapshot_xmin(pg_current_snapshot()) >= current_state.fence THEN
        current_state.published := current_state.pending;
        current_state.pending := NULL;
        current_state.fence := NULL;
    END IF;
    IF current_state.pending IS NULL THEN
        -- Sequence state is not MVCC. Read it BEFORE assigning our transaction ID. Every
        -- feature allocation at or below this value already has an older transaction ID.
        -- Snapshot xmax is insufficient: a lone open writer can have that same ID.
        -- Once xmin reaches our fence, all earlier writers have committed or rolled back.
        SELECT CASE WHEN is_called THEN last_value ELSE 0 END
        INTO allocated FROM honua.sync_generation;
        IF allocated > current_state.published THEN
            current_state.pending := allocated;
            current_state.fence := pg_current_xact_id();
        END IF;
    END IF;
    UPDATE honua.feature_change_prefix
    SET published = current_state.published, pending = current_state.pending, fence = current_state.fence
    WHERE singleton AND (published, pending, fence)
        IS DISTINCT FROM (current_state.published, current_state.pending, current_state.fence);
    RETURN current_state.published;
END;
$$;
