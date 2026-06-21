-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Row-level security (RLS) policy store (#502, epic #1275).
--
-- Backs IRlsPolicyStore so per-layer row-visibility policies survive process
-- restart and are shared across scaled nodes. A policy attaches a structured,
-- injection-safe predicate (attribute compared against a request claim) to a
-- (role, service, layer) scope; matching policies are AND-ed into the query
-- WHERE clause at read time so a user only sees the rows their role permits.
--
-- The predicate is intentionally NOT free-form SQL: it is the
-- (attribute, claim_type, comparison) tuple translated into a parameterized
-- fragment at query time, so claim contents can never inject SQL.
--
-- Idempotent: CREATE ... IF NOT EXISTS throughout so re-runs are safe and match
-- the established migration pattern.

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.rbac_rls_policies (
    policy_id     UUID         PRIMARY KEY,
    -- Role name (case-insensitive) the policy applies to; "*" matches any role.
    role          VARCHAR(128) NOT NULL,
    -- Service / layer scope; "*" wildcards match any value.
    service       VARCHAR(256) NOT NULL,
    layer         VARCHAR(256) NOT NULL,
    -- The layer attribute the predicate filters on (validated against schema).
    attribute     VARCHAR(256) NOT NULL,
    -- The claim type whose value(s) constrain the attribute.
    claim_type    VARCHAR(256) NOT NULL,
    -- Comparison applied: 0 = IN (any claim value), 1 = EQUALS (single value).
    comparison    SMALLINT     NOT NULL DEFAULT 0,
    description   TEXT,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    -- One policy per (role, service, layer, attribute, claim) scope; a layer can
    -- carry several policies (different attributes/claims) which AND together.
    CONSTRAINT rbac_rls_policies_scope_unique
        UNIQUE (role, service, layer, attribute, claim_type)
);

-- Resolution looks policies up by (service, layer) then filters by role in code,
-- so index the scope columns used in the lookup predicate.
CREATE INDEX IF NOT EXISTS idx_rbac_rls_policies_scope
    ON honua.rbac_rls_policies (service, layer);

-- Keep updated_at fresh on edits, reusing the shared trigger function defined by
-- the RBAC role store migration (defined defensively so this is self-contained).
CREATE OR REPLACE FUNCTION honua.rbac_set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_rbac_rls_policies_updated_at ON honua.rbac_rls_policies;
CREATE TRIGGER trg_rbac_rls_policies_updated_at
    BEFORE UPDATE ON honua.rbac_rls_policies
    FOR EACH ROW
    EXECUTE FUNCTION honua.rbac_set_updated_at();

COMMENT ON TABLE honua.rbac_rls_policies IS
    'Row-level security policies (#502): per (role, service, layer) row-visibility predicates AND-ed into query WHERE clauses.';
