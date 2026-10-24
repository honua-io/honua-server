-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 086_CreateNetworkTopologyEdits.sql
-- Content-editing surface for #2716 / umbrella #2656, built on the immutable generation
-- metadata from migration 084 (#2715). Adds three additive staging tables scoped to one
-- (dataset_id, generation) pair:
--   * network_topology_edge_edits       - staged edge content (geometry + attributes)
--   * network_topology_restriction_edits - staged turn-restriction content, FK-bound to
--                                          the staged edges so a dangling reference is a
--                                          database-enforced rejection, not a silent gap
--   * network_topology_edit_idempotency  - at-most-once ledger for the batched edit
--                                          endpoint, keyed by (dataset_id, generation,
--                                          idempotency_key); stores only counts/state, never
--                                          geometry or attribute values
--
-- Topology rebuild, promotion, rollback, and multi-node worker coordination are
-- deliberately not implemented by this migration (#2718/#2719/#2720). These tables are
-- pure staging content for a non-active generation; the read/solve path never queries them.
--
-- Rolling-upgrade safety: every table here is new, so old binaries are unaffected and new
-- binaries degrade gracefully (no rows) until the admin edit endpoints are exercised.

CREATE TABLE IF NOT EXISTS honua.network_topology_edge_edits (
    dataset_id        TEXT             NOT NULL,
    generation        BIGINT           NOT NULL,
    edge_id           TEXT             NOT NULL,
    source_vertex_id  TEXT             NOT NULL,
    target_vertex_id  TEXT             NOT NULL,
    geometry          geometry         NOT NULL,
    srid              INTEGER          NOT NULL,
    attributes        JSONB            NOT NULL DEFAULT '{}'::jsonb,
    created_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    created_by        TEXT,
    updated_by        TEXT,
    CONSTRAINT network_topology_edge_edits_pk
        PRIMARY KEY (dataset_id, generation, edge_id),
    CONSTRAINT network_topology_edge_edits_generation_fk
        FOREIGN KEY (dataset_id, generation)
        REFERENCES honua.network_topology_generations (dataset_id, generation)
        ON DELETE CASCADE,
    CONSTRAINT network_topology_edge_edits_srid_positive
        CHECK (srid > 0),
    CONSTRAINT network_topology_edge_edits_geometry_srid
        CHECK (ST_SRID(geometry) = srid),
    CONSTRAINT network_topology_edge_edits_geometry_type
        CHECK (GeometryType(geometry) IN ('LINESTRING', 'MULTILINESTRING'))
);

CREATE INDEX IF NOT EXISTS ix_network_topology_edge_edits_generation
    ON honua.network_topology_edge_edits (dataset_id, generation);

CREATE TABLE IF NOT EXISTS honua.network_topology_restriction_edits (
    dataset_id        TEXT             NOT NULL,
    generation        BIGINT           NOT NULL,
    restriction_id    TEXT             NOT NULL,
    from_edge_id      TEXT             NOT NULL,
    via_vertex_id     TEXT             NOT NULL,
    to_edge_id        TEXT             NOT NULL,
    kind              TEXT             NOT NULL,
    penalty           DOUBLE PRECISION,
    attributes        JSONB            NOT NULL DEFAULT '{}'::jsonb,
    created_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    created_by        TEXT,
    updated_by        TEXT,
    CONSTRAINT network_topology_restriction_edits_pk
        PRIMARY KEY (dataset_id, generation, restriction_id),
    CONSTRAINT network_topology_restriction_edits_generation_fk
        FOREIGN KEY (dataset_id, generation)
        REFERENCES honua.network_topology_generations (dataset_id, generation)
        ON DELETE CASCADE,
    -- Referential integrity against the staged edges: inserting/updating a restriction
    -- that references an unknown edge, or deleting an edge still referenced by a
    -- restriction, fails with a foreign_key_violation (23503) that the store maps to a
    -- sanitized 400 instead of leaving a dangling reference (#2716 "restriction references"
    -- validation requirement).
    CONSTRAINT network_topology_restriction_edits_from_edge_fk
        FOREIGN KEY (dataset_id, generation, from_edge_id)
        REFERENCES honua.network_topology_edge_edits (dataset_id, generation, edge_id),
    CONSTRAINT network_topology_restriction_edits_to_edge_fk
        FOREIGN KEY (dataset_id, generation, to_edge_id)
        REFERENCES honua.network_topology_edge_edits (dataset_id, generation, edge_id),
    CONSTRAINT network_topology_restriction_edits_kind_shape
        CHECK (kind IN ('prohibited', 'required', 'penalty')),
    CONSTRAINT network_topology_restriction_edits_penalty_shape
        CHECK ((kind = 'penalty') = (penalty IS NOT NULL)),
    CONSTRAINT network_topology_restriction_edits_penalty_nonnegative
        CHECK (penalty IS NULL OR penalty >= 0)
);

CREATE INDEX IF NOT EXISTS ix_network_topology_restriction_edits_generation
    ON honua.network_topology_restriction_edits (dataset_id, generation);
CREATE INDEX IF NOT EXISTS ix_network_topology_restriction_edits_from_edge
    ON honua.network_topology_restriction_edits (dataset_id, generation, from_edge_id);
CREATE INDEX IF NOT EXISTS ix_network_topology_restriction_edits_to_edge
    ON honua.network_topology_restriction_edits (dataset_id, generation, to_edge_id);

-- At-most-once ledger for the batched edit endpoint. Stores only sanitized outcome
-- metadata (state/row-version/source-revision/counts) so a replayed request can be
-- answered without re-running validation or storage, and so the ledger itself never
-- carries geometry, attribute values, or profile costs.
CREATE TABLE IF NOT EXISTS honua.network_topology_edit_idempotency (
    dataset_id              TEXT        NOT NULL,
    generation               BIGINT      NOT NULL,
    idempotency_key          TEXT        NOT NULL,
    content_hash             TEXT        NOT NULL,
    result_state             TEXT        NOT NULL,
    result_row_version       BIGINT      NOT NULL,
    result_source_revision   BIGINT      NOT NULL,
    edges_added              INTEGER     NOT NULL,
    edges_updated            INTEGER     NOT NULL,
    edges_deleted            INTEGER     NOT NULL,
    restrictions_added       INTEGER     NOT NULL,
    restrictions_updated     INTEGER     NOT NULL,
    restrictions_deleted     INTEGER     NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT network_topology_edit_idempotency_pk
        PRIMARY KEY (dataset_id, generation, idempotency_key),
    CONSTRAINT network_topology_edit_idempotency_generation_fk
        FOREIGN KEY (dataset_id, generation)
        REFERENCES honua.network_topology_generations (dataset_id, generation)
        ON DELETE CASCADE,
    CONSTRAINT network_topology_edit_idempotency_result_state_shape
        CHECK (result_state IN ('draft', 'dirty', 'building', 'ready', 'active', 'failed', 'retired'))
);

COMMENT ON TABLE honua.network_topology_edge_edits IS
    'Staged edge content for one non-active topology generation (#2716). Never read by the solve path.';
COMMENT ON TABLE honua.network_topology_restriction_edits IS
    'Staged turn-restriction content for one non-active topology generation (#2716). Never read by the solve path.';
COMMENT ON TABLE honua.network_topology_edit_idempotency IS
    'At-most-once ledger for the batched topology edit endpoint. Counts and state only - never geometry, attributes, or profile costs.';
