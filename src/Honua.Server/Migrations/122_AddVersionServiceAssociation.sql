-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.
-- Provisional migration number: verify inventory before applying.
-- Preserve every existing GUID/name/owner and leave legacy association explicit NULL.
ALTER TABLE honua.gdb_versions
    ADD COLUMN IF NOT EXISTS service_id TEXT;

CREATE INDEX IF NOT EXISTS idx_gdb_versions_service
    ON honua.gdb_versions(service_id) WHERE service_id IS NOT NULL;

COMMENT ON COLUMN honua.gdb_versions.service_id IS
    'Canonical originating metadata service ID; NULL requires explicit authorized legacy adoption for service routes';
