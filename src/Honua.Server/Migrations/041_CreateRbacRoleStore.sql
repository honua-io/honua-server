-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Persistent RBAC role/permission store (#1374).
--
-- Backs the canonical IRoleStore so role definitions, their per-operation
-- permission grants, and role memberships survive process restart and are
-- shared across scaled nodes. Replaces the process-local InMemoryRoleStore.
--
-- Idempotent: every statement is CREATE ... IF NOT EXISTS / ON CONFLICT so the
-- script is safe to re-run and matches the established migration pattern.

CREATE SCHEMA IF NOT EXISTS honua;

-- Role definitions. role_id is a client-assigned UUID (the application owns id
-- generation so built-in roles can use stable well-known ids).
CREATE TABLE IF NOT EXISTS honua.rbac_roles (
    role_id       UUID         PRIMARY KEY,
    name          VARCHAR(128) NOT NULL,
    description   TEXT,
    is_built_in   BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Role names are unique (case-insensitive) so group->role mapping and effective
-- permission resolution can look up by name deterministically.
CREATE UNIQUE INDEX IF NOT EXISTS uq_rbac_roles_name_lower
    ON honua.rbac_roles (LOWER(name));

-- Per-operation permission grants. A grant is the (service, layer, operation)
-- tuple consumed by the permission resolver. "*" is the wildcard for any of the
-- three dimensions.
CREATE TABLE IF NOT EXISTS honua.rbac_role_permissions (
    role_id       UUID         NOT NULL,
    service       VARCHAR(256) NOT NULL,
    layer         VARCHAR(256) NOT NULL,
    operation     VARCHAR(64)  NOT NULL,

    CONSTRAINT rbac_role_permissions_role_fk
        FOREIGN KEY (role_id) REFERENCES honua.rbac_roles(role_id) ON DELETE CASCADE,
    -- A grant tuple is unique within a role; SetPermissions replaces the set.
    CONSTRAINT rbac_role_permissions_unique
        UNIQUE (role_id, service, layer, operation)
);

CREATE INDEX IF NOT EXISTS idx_rbac_role_permissions_role
    ON honua.rbac_role_permissions (role_id);

-- Role memberships: which principal (user id / external subject) is in which
-- role. GetEffectivePermissionsAsync currently resolves by role name supplied
-- from claims, but the membership table is the durable source SCIM (#510) and
-- OIDC group sync (#348) write into.
CREATE TABLE IF NOT EXISTS honua.rbac_role_memberships (
    role_id       UUID         NOT NULL,
    user_id       VARCHAR(256) NOT NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT rbac_role_memberships_role_fk
        FOREIGN KEY (role_id) REFERENCES honua.rbac_roles(role_id) ON DELETE CASCADE,
    CONSTRAINT rbac_role_memberships_pk PRIMARY KEY (role_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_rbac_role_memberships_user
    ON honua.rbac_role_memberships (user_id);

-- Keep updated_at fresh on role edits (reuses the shared trigger function when
-- present; defined defensively here so this migration is self-contained).
CREATE OR REPLACE FUNCTION honua.rbac_set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_rbac_roles_updated_at ON honua.rbac_roles;
CREATE TRIGGER trg_rbac_roles_updated_at
    BEFORE UPDATE ON honua.rbac_roles
    FOR EACH ROW
    EXECUTE FUNCTION honua.rbac_set_updated_at();

-- Seed built-in roles idempotently with stable well-known ids (these match the
-- ids the InMemoryRoleStore used so existing references stay valid). Built-in
-- roles cannot be deleted (enforced in the store + by is_built_in).
INSERT INTO honua.rbac_roles (role_id, name, description, is_built_in)
VALUES
    ('00000000-0000-0000-0000-000000000001', 'admin',  'Full administrative access', TRUE),
    ('00000000-0000-0000-0000-000000000003', 'editor', 'Read and write access to all services and layers', TRUE),
    ('00000000-0000-0000-0000-000000000002', 'viewer', 'Read-only access to all services and layers', TRUE)
ON CONFLICT (role_id) DO NOTHING;

-- Seed built-in grants. admin => everything; editor => read + the write
-- operations; viewer => read only. ON CONFLICT keeps re-runs idempotent.
INSERT INTO honua.rbac_role_permissions (role_id, service, layer, operation)
VALUES
    -- admin: wildcard operation on everything.
    ('00000000-0000-0000-0000-000000000001', '*', '*', '*'),
    -- editor: read + insert/update/delete + export on everything.
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'query'),
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'insert'),
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'update'),
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'delete'),
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'export'),
    ('00000000-0000-0000-0000-000000000003', '*', '*', 'metadata'),
    -- viewer: read only.
    ('00000000-0000-0000-0000-000000000002', '*', '*', 'query'),
    ('00000000-0000-0000-0000-000000000002', '*', '*', 'metadata')
ON CONFLICT (role_id, service, layer, operation) DO NOTHING;

COMMENT ON TABLE honua.rbac_roles IS
    'RBAC role definitions (#1374). Built-in roles (is_built_in) cannot be deleted.';
COMMENT ON TABLE honua.rbac_role_permissions IS
    'Per-operation permission grants: (service, layer, operation) tuples consumed by the permission resolver (#1375).';
COMMENT ON TABLE honua.rbac_role_memberships IS
    'Durable principal->role memberships written by OIDC group sync (#348) and SCIM (#510).';
