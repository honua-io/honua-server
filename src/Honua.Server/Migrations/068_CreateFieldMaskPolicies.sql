-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Field-level security (column masking) policy store (#1940, follow-up to RLS #502).
--
-- Backs IFieldMaskPolicyStore so per-layer attribute-masking policies survive process
-- restart and are shared across scaled nodes. A policy attaches a masked attribute
-- (column) to a (role, service, layer) scope; matching policies are unioned and the
-- masked attributes are dropped from query output server-side so a restricted role never
-- receives those columns' values — regardless of the outFields / $select / properties the
-- client requested.
--
-- The masked attribute is removed at the shared projection seam inside the feature store
-- (parallel to the EnforcedSqlFilter chokepoint used for rows in #502), so a single
-- enforcement point covers every query protocol (GeoServices, OGC, OData) and output
-- format. The field name is bound as a query parameter, so it can never inject SQL.
--
-- Idempotent: CREATE ... IF NOT EXISTS throughout so re-runs are safe and match the
-- established migration pattern.

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.rbac_field_mask_policies (
    policy_id     UUID         PRIMARY KEY,
    -- Role name (case-insensitive) the policy applies to; "*" matches any role.
    role          VARCHAR(128) NOT NULL,
    -- Service / layer scope; "*" wildcards match any value.
    service       VARCHAR(256) NOT NULL,
    layer         VARCHAR(256) NOT NULL,
    -- The layer attribute (column) that is masked (dropped) from query output.
    attribute     VARCHAR(256) NOT NULL,
    description   TEXT,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    -- One policy per (role, service, layer, attribute) scope; a layer can carry several
    -- policies (different attributes/roles) which union together.
    CONSTRAINT rbac_field_mask_policies_scope_unique
        UNIQUE (role, service, layer, attribute)
);

-- Resolution looks policies up by (service, layer) then filters by role in code,
-- so index the scope columns used in the lookup predicate.
CREATE INDEX IF NOT EXISTS idx_rbac_field_mask_policies_scope
    ON honua.rbac_field_mask_policies (service, layer);

-- Keep updated_at fresh on edits, reusing the shared trigger function defined by the
-- RBAC role store migration (defined defensively so this is self-contained).
CREATE OR REPLACE FUNCTION honua.rbac_set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_rbac_field_mask_policies_updated_at ON honua.rbac_field_mask_policies;
CREATE TRIGGER trg_rbac_field_mask_policies_updated_at
    BEFORE UPDATE ON honua.rbac_field_mask_policies
    FOR EACH ROW
    EXECUTE FUNCTION honua.rbac_set_updated_at();

COMMENT ON TABLE honua.rbac_field_mask_policies IS
    'Field-level security policies (#1940): per (role, service, layer) attribute-masking rules dropped from query output at the shared projection seam.';
