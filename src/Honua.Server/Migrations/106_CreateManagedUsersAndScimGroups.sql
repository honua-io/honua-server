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
-- and matches the established migration pattern. $HonuaSchema$ is the DbUp variable the
-- migration runner substitutes with the configured metadata schema (Database:Schema,
-- default "honua"), so a custom-schema deployment creates these tables where the
-- QualifyTable-based stores read them.
--
-- The migration-skipping integration-test host never runs this script; its tables are
-- hand-mirrored into tests/seed/server.yaml (SeedRbacTableParityTests guard,
-- honua-server#1568 pattern). Keep the two in sync.

CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

-- Managed user identities. user_id is the SCIM userName for SCIM-provisioned users (the
-- IdP-owned login identifier, reused as the stable record id so re-provisioning stays
-- idempotent on the same key — mirrors the in-memory store contract).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.managed_users (
    user_id             VARCHAR(256) PRIMARY KEY,
    external_id         VARCHAR(256),
    external_issuer     VARCHAR(2048),
    display_name        VARCHAR(512) NOT NULL,
    email               VARCHAR(320),
    provisioning_source VARCHAR(64)  NOT NULL,
    provider_id         UUID,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

ALTER TABLE $HonuaSchema$.managed_users
    ADD COLUMN IF NOT EXISTS external_issuer VARCHAR(2048);

-- Lookups are case-insensitive (parity with the in-memory store's OrdinalIgnoreCase keys).
CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_user_id_lower
    ON $HonuaSchema$.managed_users (LOWER(user_id));

-- OIDC subjects are case-sensitive and unique only within an issuer. Index the composite
-- identity so two configured issuers may legitimately provision the same sub without either
-- identity resolving the other's roles (#3141 review). Legacy issuer-less subjects remain
-- unique within their own namespace.
DROP INDEX IF EXISTS $HonuaSchema$.uq_managed_users_external_id_lower;

CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_external_identity
    ON $HonuaSchema$.managed_users (external_issuer, external_id)
    WHERE external_id IS NOT NULL AND external_issuer IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_external_id_legacy
    ON $HonuaSchema$.managed_users (external_id)
    WHERE external_id IS NOT NULL AND external_issuer IS NULL;

CREATE INDEX IF NOT EXISTS ix_managed_users_external_id
    ON $HonuaSchema$.managed_users (external_id)
    WHERE external_id IS NOT NULL;

-- Role assignments (flat role-name set per user; SCIM group sync and the admin
-- role-update surface both mutate this table).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.managed_user_roles (
    user_id    VARCHAR(256) NOT NULL REFERENCES $HonuaSchema$.managed_users(user_id) ON DELETE CASCADE,
    role       VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_user_roles_user_role_lower
    ON $HonuaSchema$.managed_user_roles (user_id, LOWER(role));

-- Admin list supports filtering by role membership.
CREATE INDEX IF NOT EXISTS ix_managed_user_roles_role_lower
    ON $HonuaSchema$.managed_user_roles (LOWER(role));

-- SCIM 2.0 groups (#510). Each group maps to the Honua role named by display_name.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.scim_groups (
    group_id     VARCHAR(64)  PRIMARY KEY,
    display_name VARCHAR(256) NOT NULL,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_groups_display_name_lower
    ON $HonuaSchema$.scim_groups (LOWER(display_name));

-- Group membership. Deliberately NOT foreign-keyed to managed_users: an IdP may push
-- group members before the corresponding users are provisioned (the in-memory store
-- kept such members in the group and treated the role sync as a no-op; parity here).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.scim_group_members (
    group_id   VARCHAR(64)  NOT NULL REFERENCES $HonuaSchema$.scim_groups(group_id) ON DELETE CASCADE,
    user_id    VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_group_members_group_user_lower
    ON $HonuaSchema$.scim_group_members (group_id, LOWER(user_id));

CREATE INDEX IF NOT EXISTS ix_scim_group_members_user_lower
    ON $HonuaSchema$.scim_group_members (LOWER(user_id));
