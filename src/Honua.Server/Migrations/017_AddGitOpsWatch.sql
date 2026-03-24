-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 017_AddGitOpsWatch.sql
-- Description: Adds tables for GitOps git repository watching and change tracking.
-- Dependencies: Requires honua schema from 001_CreateHonuaSchema.sql.

CREATE TABLE IF NOT EXISTS honua.gitops_watch_configs (
    config_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    repository_url TEXT NOT NULL,
    branch TEXT NOT NULL DEFAULT 'main',
    manifest_path TEXT NOT NULL DEFAULT 'manifests/',
    poll_interval_seconds INT NOT NULL DEFAULT 60,
    approval_required BOOLEAN NOT NULL DEFAULT FALSE,
    prune_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    last_known_commit_sha TEXT,
    last_polled_at TIMESTAMPTZ,
    configured_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Enforce single-config model: only one watch configuration row is allowed.
CREATE UNIQUE INDEX IF NOT EXISTS idx_gitops_watch_configs_singleton
    ON honua.gitops_watch_configs ((TRUE));

CREATE TABLE IF NOT EXISTS honua.gitops_change_records (
    change_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    config_id UUID NOT NULL REFERENCES honua.gitops_watch_configs(config_id) ON DELETE CASCADE,
    commit_sha TEXT NOT NULL,
    commit_message TEXT,
    commit_author TEXT,
    commit_timestamp TIMESTAMPTZ,
    manifest_before JSONB,
    manifest_after JSONB NOT NULL,
    status TEXT NOT NULL DEFAULT 'applied',
    pending_approval_id UUID,
    apply_summary TEXT,
    error_message TEXT,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    applied_at TIMESTAMPTZ,
    CONSTRAINT gitops_change_records_valid_status
        CHECK (status IN ('applied', 'pending_approval', 'failed', 'skipped'))
);

CREATE INDEX IF NOT EXISTS idx_gitops_change_records_config
    ON honua.gitops_change_records(config_id);
CREATE INDEX IF NOT EXISTS idx_gitops_change_records_detected
    ON honua.gitops_change_records(detected_at DESC);
CREATE INDEX IF NOT EXISTS idx_gitops_change_records_status
    ON honua.gitops_change_records(status);
CREATE INDEX IF NOT EXISTS idx_gitops_change_records_approval
    ON honua.gitops_change_records(pending_approval_id) WHERE pending_approval_id IS NOT NULL;
