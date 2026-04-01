-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 018_AddMigrationEvidenceReports.sql
-- Description: Adds durable immutable JSONB artifacts for migration evidence reports.
-- Dependencies: Requires honua schema from 001_CreateHonuaSchema.sql.

CREATE TABLE IF NOT EXISTS honua.migration_evidence_reports (
    report_id UUID PRIMARY KEY,
    provider TEXT NOT NULL,
    cutover_profile TEXT NOT NULL,
    readiness TEXT NOT NULL,
    source_service_url TEXT,
    target_base_url TEXT NOT NULL,
    target_service_name TEXT NOT NULL,
    inventory_artifact_ref TEXT,
    translation_manifest_ref TEXT,
    import_job_id TEXT,
    report_hash TEXT NOT NULL,
    generated_by TEXT,
    generated_at TIMESTAMPTZ NOT NULL,
    warnings_count INT NOT NULL,
    blockers_count INT NOT NULL,
    artifact JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_migration_evidence_reports_generated_at
    ON honua.migration_evidence_reports(generated_at DESC);
CREATE INDEX IF NOT EXISTS idx_migration_evidence_reports_filters
    ON honua.migration_evidence_reports(provider, cutover_profile, readiness);
CREATE INDEX IF NOT EXISTS idx_migration_evidence_reports_target_service
    ON honua.migration_evidence_reports(target_service_name);
