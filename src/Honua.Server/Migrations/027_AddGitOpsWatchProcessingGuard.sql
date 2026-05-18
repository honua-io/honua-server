-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 027_AddGitOpsWatchProcessingGuard.sql
-- Description: Adds a leased processing guard for GitOps watch commits.
-- Dependencies: Requires 017_AddGitOpsWatch.sql.

ALTER TABLE honua.gitops_watch_configs
    ADD COLUMN IF NOT EXISTS processing_commit_sha TEXT,
    ADD COLUMN IF NOT EXISTS processing_lease_id UUID,
    ADD COLUMN IF NOT EXISTS processing_started_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS processing_lease_expires_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_gitops_watch_configs_processing_lease
    ON honua.gitops_watch_configs(processing_lease_expires_at)
    WHERE processing_commit_sha IS NOT NULL;

COMMENT ON COLUMN honua.gitops_watch_configs.processing_commit_sha IS
    'Commit SHA currently leased for GitOps watch processing.';
COMMENT ON COLUMN honua.gitops_watch_configs.processing_lease_id IS
    'Opaque lease token held by the node processing processing_commit_sha.';
COMMENT ON COLUMN honua.gitops_watch_configs.processing_started_at IS
    'Timestamp when the current GitOps watch processing lease was acquired.';
COMMENT ON COLUMN honua.gitops_watch_configs.processing_lease_expires_at IS
    'Timestamp after which another node may acquire GitOps watch processing for a commit.';
