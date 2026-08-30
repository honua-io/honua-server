-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Role memberships in managed_user_roles retain globally unique role names.
-- Soft deletion keeps those names reserved forever, preventing a stale membership
-- from silently attaching to a different role after delete/recreate races.

ALTER TABLE IF EXISTS $HonuaSchema$.rbac_roles
    ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_rbac_roles_active_name
    ON $HonuaSchema$.rbac_roles (name)
    WHERE deleted_at IS NULL;

COMMENT ON COLUMN $HonuaSchema$.rbac_roles.deleted_at IS
    'Soft-delete tombstone. The row and globally unique name remain reserved so name-based memberships cannot reattach.';
