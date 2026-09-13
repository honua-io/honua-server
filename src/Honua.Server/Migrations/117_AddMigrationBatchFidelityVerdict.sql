-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 117_AddMigrationBatchFidelityVerdict.sql
-- Description: Persists the service-level migration fidelity verdict on batch (service) imports
--              and the per-layer verdict on each child (#4600).
-- Dependencies: 045_CreateMigrationBatchRuns.sql
--
-- A batch rolled up child statuses only. A layer that completed without its reconciliation
-- checks counted as a success, and relationship apply could defer every relationship while the
-- batch still reported 'succeeded'. The fidelity verdict answers "is the migrated service proven
-- equivalent to the source" separately from "did the batch finish"; fidelity_differences holds
-- the ordered, stable-coded per-resource differences that produced it. Both are written once, at
-- the terminal transition, so existing rows keep NULL (no verdict was ever computed for them).

ALTER TABLE honua.migration_batch_runs
    ADD COLUMN IF NOT EXISTS fidelity_verdict VARCHAR(32),
    ADD COLUMN IF NOT EXISTS fidelity_differences JSONB;

ALTER TABLE honua.migration_batch_children
    ADD COLUMN IF NOT EXISTS fidelity_verdict VARCHAR(32);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_migration_batch_runs_fidelity_verdict'
          AND conrelid = 'honua.migration_batch_runs'::regclass) THEN
        ALTER TABLE honua.migration_batch_runs
            ADD CONSTRAINT chk_migration_batch_runs_fidelity_verdict
            CHECK (fidelity_verdict IS NULL OR fidelity_verdict IN ('full-fidelity','unverified','incomplete'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_migration_batch_children_fidelity_verdict'
          AND conrelid = 'honua.migration_batch_children'::regclass) THEN
        ALTER TABLE honua.migration_batch_children
            ADD CONSTRAINT chk_migration_batch_children_fidelity_verdict
            CHECK (fidelity_verdict IS NULL OR fidelity_verdict IN ('full-fidelity','unverified','incomplete'));
    END IF;
END $$;

COMMENT ON COLUMN honua.migration_batch_runs.fidelity_verdict IS
    'Service-level migration fidelity verdict (#4600): full-fidelity, unverified or incomplete. NULL while running.';

COMMENT ON COLUMN honua.migration_batch_runs.fidelity_differences IS
    'Ordered per-resource fidelity differences (code, severity, subject, expected, actual, summary) that produced fidelity_verdict.';

COMMENT ON COLUMN honua.migration_batch_children.fidelity_verdict IS
    'Per-layer fidelity verdict reported by the child import job (#4600). NULL until the child is terminal or when the job recorded none.';
