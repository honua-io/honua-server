-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable canonical stores backing the MCP promotion resources (#2482). Full
-- domain records are preserved as JSONB for wire-compatible evolution, while
-- lifecycle/source/target columns keep the store interfaces' list operations
-- indexed and avoid JSON scans.

CREATE TABLE IF NOT EXISTS honua.promotion_published_services (
    service_id   TEXT        NOT NULL PRIMARY KEY,
    intent_id    TEXT        NOT NULL,
    source_kind  TEXT        NOT NULL,
    source_id    TEXT        NOT NULL,
    target_kind  TEXT        NOT NULL,
    status       TEXT        NOT NULL,
    document     JSONB       NOT NULL,
    published_at TIMESTAMPTZ NOT NULL,
    updated_at   TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_promotion_published_services_active
    ON honua.promotion_published_services (status, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_promotion_published_services_source
    ON honua.promotion_published_services (source_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS honua.promotion_deployments (
    deployment_id TEXT        NOT NULL PRIMARY KEY,
    source_kind   TEXT        NOT NULL,
    source_id     TEXT        NOT NULL,
    target_id     TEXT        NOT NULL,
    status        TEXT        NOT NULL,
    document      JSONB       NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL,
    updated_at    TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_promotion_deployments_active
    ON honua.promotion_deployments (status, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_promotion_deployments_source
    ON honua.promotion_deployments (source_kind, source_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_promotion_deployments_target
    ON honua.promotion_deployments (target_id, updated_at DESC);

COMMENT ON TABLE honua.promotion_published_services IS
    'Canonical durable published-service records backing honua://published-services (#2482).';
COMMENT ON TABLE honua.promotion_deployments IS
    'Canonical durable deployment lifecycle records backing promotion MCP resources (#2482).';
