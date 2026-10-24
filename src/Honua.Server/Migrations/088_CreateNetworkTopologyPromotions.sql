-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 088_CreateNetworkTopologyPromotions.sql
-- Immutable active-generation promotion/rollback history (#2719), built on the generation
-- lifecycle from migration 084 (#2715) and the rebuild attempts from migration 087
-- (#2718). Promotion/rollback application logic (atomic active-pointer flip, evidence
-- verification, honua.network_datasets repoint) lives in application code
-- (PostgresNetworkTopologyPromotionStore); this migration only provisions the durable
-- history table. The read/solve path never queries this table.

CREATE TABLE IF NOT EXISTS honua.network_topology_promotions (
    promotion_id      TEXT        NOT NULL PRIMARY KEY,
    dataset_id        TEXT        NOT NULL,
    from_generation   BIGINT,
    to_generation     BIGINT      NOT NULL,
    kind              TEXT        NOT NULL,
    actor             TEXT        NOT NULL,
    reason            TEXT,
    idempotency_key   TEXT        NOT NULL,
    evidence_digest   TEXT,
    promoted_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT network_topology_promotions_dataset_fk
        FOREIGN KEY (dataset_id) REFERENCES honua.network_datasets (id) ON DELETE CASCADE,
    CONSTRAINT network_topology_promotions_valid_kind
        CHECK (kind IN ('promote', 'rollback')),
    CONSTRAINT network_topology_promotions_positive_to_generation
        CHECK (to_generation > 0)
);

-- At-most-once idempotency per dataset: replaying the same client-supplied key returns the
-- original history entry rather than re-promoting.
CREATE UNIQUE INDEX IF NOT EXISTS ux_network_topology_promotions_idempotency
    ON honua.network_topology_promotions (dataset_id, idempotency_key);

CREATE INDEX IF NOT EXISTS ix_network_topology_promotions_dataset_history
    ON honua.network_topology_promotions (dataset_id, promoted_at DESC);

COMMENT ON TABLE honua.network_topology_promotions IS
    'Immutable active-generation promotion/rollback history (#2719). Never mutated after insert.';
