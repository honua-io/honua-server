-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 044: Add reconciliation-scorecard columns to migration_runs
-- Issue #1381. The signed reconciliation scorecard aggregates a run's
-- per-layer data-reconciliation outcome (kept distinct from capability
-- parity) and is fingerprinted with the same SHA-256-over-canonical-JSON
-- mechanism as the slice-4 evidence pack. These columns let the admin
-- orchestration endpoints stream the scorecard back and prove it was not
-- altered, mirroring the evidence_pack_ref/fingerprint/body columns added
-- in migration 031.
-- Dependencies: Requires honua.migration_runs from 031_CreateMigrationRunCatalog.sql.

ALTER TABLE honua.migration_runs
    ADD COLUMN IF NOT EXISTS reconciliation_scorecard_fingerprint VARCHAR(128),
    ADD COLUMN IF NOT EXISTS reconciliation_scorecard_body        JSONB;

COMMENT ON COLUMN honua.migration_runs.reconciliation_scorecard_fingerprint IS
    'SHA-256 fingerprint (sha256:<hex>) of the signed reconciliation scorecard for this run (issue #1381). Returned as an ETag so callers can prove they received the same scorecard later.';

COMMENT ON COLUMN honua.migration_runs.reconciliation_scorecard_body IS
    'Signed reconciliation scorecard JSON body (issue #1381). jsonb so reviewers can introspect the per-layer data-reconciliation and capability-parity roll-up directly with SQL when triaging a NeedsReview run.';
