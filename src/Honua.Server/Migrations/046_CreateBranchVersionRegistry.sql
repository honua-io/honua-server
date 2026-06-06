-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 046_CreateBranchVersionRegistry.sql
-- Description: Adds the branch-version registry that backs gdbVersion routing for
--              branch-versioned editing. A named branch version maps a service's base
--              (DEFAULT) storage layer id to a distinct synthetic branch storage layer id.
--              Branch feature rows live in the shared features table under the branch
--              layer id, so they are isolated from DEFAULT and are tracked + synchronised
--              by the existing change-tracking / replication pipeline (012) without extra
--              branch-specific plumbing.
-- Dependencies: Requires honua schema and the features table from 001_CreateHonuaSchema.sql.

-- Synthetic storage layer ids for branch versions are allocated from a dedicated high
-- range so they never collide with honua.layers.layer_id (SERIAL, allocated from 1).
CREATE SEQUENCE IF NOT EXISTS honua.branch_layer_id_seq
    AS INTEGER START WITH 1000000000 INCREMENT BY 1 NO CYCLE;

CREATE TABLE IF NOT EXISTS honua.gdb_versions (
    service_id TEXT NOT NULL,
    version_name TEXT NOT NULL,
    version_name_lower TEXT NOT NULL,
    base_layer_id INT NOT NULL,
    branch_layer_id INT NOT NULL DEFAULT nextval('honua.branch_layer_id_seq'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (service_id, version_name_lower, base_layer_id),
    CONSTRAINT gdb_versions_valid_service CHECK (LENGTH(service_id) > 0),
    CONSTRAINT gdb_versions_valid_name CHECK (LENGTH(version_name) > 0 AND LENGTH(version_name) <= 256),
    CONSTRAINT gdb_versions_not_default CHECK (LOWER(version_name) NOT IN ('default', 'sde.default')),
    CONSTRAINT gdb_versions_unique_branch_layer UNIQUE (branch_layer_id)
);

CREATE INDEX IF NOT EXISTS idx_gdb_versions_service
    ON honua.gdb_versions(service_id);

COMMENT ON SEQUENCE honua.branch_layer_id_seq IS 'Allocates synthetic storage layer ids for branch versions, isolated from honua.layers.layer_id';
COMMENT ON TABLE honua.gdb_versions IS 'Branch-version registry for gdbVersion-routed branch-versioned editing';
COMMENT ON COLUMN honua.gdb_versions.service_id IS 'Feature service the branch version belongs to';
COMMENT ON COLUMN honua.gdb_versions.version_name IS 'Branch version name as supplied by clients via gdbVersion';
COMMENT ON COLUMN honua.gdb_versions.version_name_lower IS 'Case-insensitive lookup key for the branch version name';
COMMENT ON COLUMN honua.gdb_versions.base_layer_id IS 'Base (DEFAULT) storage layer id the branch was forked from';
COMMENT ON COLUMN honua.gdb_versions.branch_layer_id IS 'Synthetic storage layer id that isolates branch feature rows from DEFAULT';
COMMENT ON COLUMN honua.gdb_versions.created_at IS 'Timestamp when the branch version was created';
</content>
</invoke>
