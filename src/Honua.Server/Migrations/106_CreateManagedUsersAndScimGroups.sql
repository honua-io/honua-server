-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable, shared managed-user and SCIM group store (#3141).
--
-- Backs the canonical IUserStore / IScimUserStore / IScimGroupStore so managed-identity
-- membership (SCIM provisioning, role assignment, deactivation) survives process restart
-- and is shared across scaled nodes. Replaces the process-local InMemoryUserStore /
-- InMemoryScimGroupStore as the registered implementations on Postgres profiles, making
-- ManagedUserPrincipalMembershipSource misses authoritative for the deferred-lane
-- fail-closed control introduced in #3119 (root cause #3081).
--
-- Idempotent: every statement is CREATE ... IF NOT EXISTS so the script is safe to re-run
-- and matches the established migration pattern.

CREATE SCHEMA IF NOT EXISTS honua;

-- Managed user identities. user_id is the SCIM userName for SCIM-provisioned users (the
-- IdP-owned login identifier, reused as the stable record id so re-provisioning stays
-- idempotent on the same key — mirrors the in-memory store contract).
CREATE TABLE IF NOT EXISTS honua.managed_users (
    user_id             VARCHAR(256) PRIMARY KEY,
    external_id         VARCHAR(256),
    display_name        VARCHAR(512) NOT NULL,
    email               VARCHAR(320),
    provisioning_source VARCHAR(64)  NOT NULL,
    provider_id         UUID,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Lookups are case-insensitive (parity with the in-memory store's OrdinalIgnoreCase keys).
CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_user_id_lower
    ON honua.managed_users (LOWER(user_id));

-- The stable external subject (SCIM externalId / OIDC sub). Indexed and unique so deferred
-- security snapshots capturing the OIDC subject resolve to the same record as the SCIM
-- userName (#3141 finding 2). Partial: multiple users may omit an external id.
CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_external_id_lower
    ON honua.managed_users (LOWER(external_id))
    WHERE external_id IS NOT NULL;

-- Role assignments (flat role-name set per user; SCIM group sync and the admin
-- role-update surface both mutate this table).
CREATE TABLE IF NOT EXISTS honua.managed_user_roles (
    user_id    VARCHAR(256) NOT NULL REFERENCES honua.managed_users(user_id) ON DELETE CASCADE,
    role       VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_user_roles_user_role_lower
    ON honua.managed_user_roles (user_id, LOWER(role));

-- Admin list supports filtering by role membership.
CREATE INDEX IF NOT EXISTS ix_managed_user_roles_role_lower
    ON honua.managed_user_roles (LOWER(role));

-- SCIM 2.0 groups (#510). Each group maps to the Honua role named by display_name.
CREATE TABLE IF NOT EXISTS honua.scim_groups (
    group_id     VARCHAR(64)  PRIMARY KEY,
    display_name VARCHAR(256) NOT NULL,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_groups_display_name_lower
    ON honua.scim_groups (LOWER(display_name));

-- Group membership. Deliberately NOT foreign-keyed to managed_users: an IdP may push
-- group members before the corresponding users are provisioned (the in-memory store
-- kept such members in the group and treated the role sync as a no-op; parity here).
CREATE TABLE IF NOT EXISTS honua.scim_group_members (
    group_id   VARCHAR(64)  NOT NULL REFERENCES honua.scim_groups(group_id) ON DELETE CASCADE,
    user_id    VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_group_members_group_user_lower
    ON honua.scim_group_members (group_id, LOWER(user_id));

CREATE INDEX IF NOT EXISTS ix_scim_group_members_user_lower
    ON honua.scim_group_members (LOWER(user_id));
