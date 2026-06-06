-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 047_CreateGdbVersions.sql
-- Description: Adds the branch-versioning version dimension (#1272 Track B). A gdb version is a
--              named branch off DEFAULT that accumulates isolated edits which are later reconciled
--              against and posted back to DEFAULT. This migration creates only the version registry;
--              the change-log version dimension and version-aware trigger are added in 048. The
--              DEFAULT version is implicit (NULL version_id everywhere) and is intentionally NOT
--              represented as a row so existing NULL-version read/write paths stay byte-identical.
-- Dependencies: Requires honua schema (001) and honua.sync_generation (012_AddReplicationDurability).

CREATE TABLE IF NOT EXISTS honua.gdb_versions (
    version_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    -- Esri-style "owner.name" identity (e.g. "sde.QA"). Unique per owner+name.
    version_name TEXT NOT NULL,
    owner TEXT NOT NULL,
    -- Parent version this branch was created from. NULL parent == branched from DEFAULT.
    parent_version UUID REFERENCES honua.gdb_versions(version_id) ON DELETE SET NULL,
    -- Access level (VersionAccess ordinal): 0=private, 1=protected, 2=public.
    access SMALLINT NOT NULL DEFAULT 0,
    -- Lifecycle state (VersionState ordinal): 0=active, 1=reconciling, 2=posting, 3=deleted.
    state SMALLINT NOT NULL DEFAULT 0,
    -- Generation pointers used by reconcile/post. common_ancestor_gen is the DEFAULT generation the
    -- branch last reconciled against (its merge base); branch_gen is the latest generation produced
    -- by edits within this version. Both advance through the version-aware change log (048).
    common_ancestor_gen BIGINT NOT NULL DEFAULT 0,
    branch_gen BIGINT NOT NULL DEFAULT 0,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT gdb_versions_valid_name CHECK(LENGTH(version_name) > 0 AND LENGTH(version_name) <= 256),
    CONSTRAINT gdb_versions_valid_owner CHECK(LENGTH(owner) > 0),
    CONSTRAINT gdb_versions_valid_access CHECK(access BETWEEN 0 AND 2),
    CONSTRAINT gdb_versions_valid_state CHECK(state BETWEEN 0 AND 3)
);

-- Version identity is unique per owner+name (case-insensitive, matching Esri "owner.name" lookup).
CREATE UNIQUE INDEX IF NOT EXISTS idx_gdb_versions_owner_name
    ON honua.gdb_versions(LOWER(owner), LOWER(version_name));

-- Parent traversal for reconcile-base resolution and version-tree listing.
CREATE INDEX IF NOT EXISTS idx_gdb_versions_parent
    ON honua.gdb_versions(parent_version);

COMMENT ON TABLE honua.gdb_versions IS 'Branch-versioning version registry; DEFAULT is implicit NULL (#1272)';
COMMENT ON COLUMN honua.gdb_versions.version_id IS 'Unique version identifier';
COMMENT ON COLUMN honua.gdb_versions.version_name IS 'Esri-style owner.name version identity';
COMMENT ON COLUMN honua.gdb_versions.owner IS 'Version owner';
COMMENT ON COLUMN honua.gdb_versions.parent_version IS 'Parent version; NULL == branched from DEFAULT';
COMMENT ON COLUMN honua.gdb_versions.access IS 'Access level (0=private,1=protected,2=public)';
COMMENT ON COLUMN honua.gdb_versions.state IS 'Lifecycle state (0=active,1=reconciling,2=posting,3=deleted)';
COMMENT ON COLUMN honua.gdb_versions.common_ancestor_gen IS 'DEFAULT generation the branch last reconciled against (merge base)';
COMMENT ON COLUMN honua.gdb_versions.branch_gen IS 'Latest generation produced by edits within this version';
