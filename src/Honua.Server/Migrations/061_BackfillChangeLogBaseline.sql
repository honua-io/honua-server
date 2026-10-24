-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 061_BackfillChangeLogBaseline.sql
-- Description: Seeds a baseline change-log entry for every feature that predates change tracking
--              (migration 012) and therefore has no row in honua.feature_changes. Before this
--              migration the replica download path special-cased "no change-log coverage" as
--              "return the whole table as adds on every gen-0 sync", which is correct for a true
--              first sync but wrong for pre-migration data that has since changed: such rows have
--              no gen-0 baseline entry, so a replica created against old data could miss early
--              history and could not be served pure incremental deltas. By inserting a single
--              baseline Insert (operation = 1) per uncovered feature at one captured generation,
--              the gen-0 extract resolves those features through the normal change-log delta path
--              as adds exactly once, and every subsequent edit advances to a higher generation so
--              the replica receives pure deltas thereafter (#1876).
--
--              The baseline rows are inserted directly into honua.feature_changes (not into the
--              features table), so the honua.track_feature_changes trigger does not fire and no
--              recursion or double-counting occurs. The insert is guarded by NOT EXISTS against the
--              existing change log, so the migration is idempotent and only ever covers genuinely
--              untracked features; features that already have any change history (the common case
--              for data created after migration 012) are left untouched and keep their real history.
-- Dependencies: Requires honua.feature_changes, honua.sync_generation, and the features table
--               (012_AddReplicationDurability.sql, 001_CreateHonuaSchema.sql).

DO $$
DECLARE
    baseline_gen BIGINT;
    seeded_count BIGINT;
BEGIN
    -- Nothing to baseline when there are no features at all, or when every feature already has
    -- change-log coverage. Probe first so a fully-tracked database skips the sequence bump entirely
    -- and the migration stays a no-op on re-run.
    IF NOT EXISTS (
        SELECT 1
        FROM features f
        WHERE NOT EXISTS (
            SELECT 1
            FROM honua.feature_changes c
            WHERE c.layer_id = f.layer_id
              AND c.objectid = f.objectid
        )
    ) THEN
        RAISE NOTICE 'Change-log baseline backfill: no untracked features; skipping.';
        RETURN;
    END IF;

    -- Capture a single baseline generation for the whole backfill so all baselined features share
    -- one cutover point. A replica whose base generation is below this value receives the baselined
    -- features as adds once (via the gen-0 / since-baseline delta) and pure deltas afterward.
    baseline_gen := nextval('honua.sync_generation');

    INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at)
    SELECT baseline_gen, f.layer_id, f.objectid, 1, now()
    FROM features f
    WHERE NOT EXISTS (
        SELECT 1
        FROM honua.feature_changes c
        WHERE c.layer_id = f.layer_id
          AND c.objectid = f.objectid
    );

    GET DIAGNOSTICS seeded_count = ROW_COUNT;
    RAISE NOTICE 'Change-log baseline backfill: seeded % baseline change rows at generation %.',
        seeded_count, baseline_gen;
END $$;
