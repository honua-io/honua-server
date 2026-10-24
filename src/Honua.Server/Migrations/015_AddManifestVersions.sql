-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Manifest version history for GitOps drift detection and rollback (#515)

CREATE TABLE IF NOT EXISTS honua.manifest_versions (
    version_id TEXT PRIMARY KEY,
    manifest_hash TEXT NOT NULL,
    manifest_json JSONB NOT NULL,
    summary TEXT,
    actor TEXT,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resource_count INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_manifest_versions_applied_at
    ON honua.manifest_versions(applied_at DESC);
CREATE INDEX IF NOT EXISTS idx_manifest_versions_manifest_hash
    ON honua.manifest_versions(manifest_hash);
