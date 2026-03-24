-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 016_AddManifestPendingChanges.sql
-- Description: Adds pending approval table for GitOps manifest approval workflows.
-- Dependencies: Requires honua schema and metadata_resources from 010_AddMetadataResources.sql.

CREATE TABLE IF NOT EXISTS honua.manifest_pending_changes (
    pending_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    manifest_snapshot JSONB NOT NULL,
    manifest_hash TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    requested_by TEXT,
    requested_reason TEXT,
    decision_by TEXT,
    decision_reason TEXT,
    dry_run BOOLEAN NOT NULL DEFAULT FALSE,
    prune BOOLEAN NOT NULL DEFAULT FALSE,
    resource_count INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    decided_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    CONSTRAINT manifest_pending_changes_valid_status
        CHECK (status IN ('pending', 'applying', 'approved', 'rejected', 'expired'))
);

CREATE INDEX IF NOT EXISTS idx_manifest_pending_changes_status
    ON honua.manifest_pending_changes(status);
CREATE INDEX IF NOT EXISTS idx_manifest_pending_changes_created
    ON honua.manifest_pending_changes(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_manifest_pending_changes_expires
    ON honua.manifest_pending_changes(expires_at)
    WHERE status = 'pending' AND expires_at IS NOT NULL;
