-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 087_CreateNetworkTopologyRebuildAttempts.sql
-- Durable shadow-topology rebuild attempts (#2718) and their multi-node fencing lease
-- (#2720), built on the immutable generation metadata from migration 084 (#2715) and the
-- staged content edits from migration 086 (#2716). Two additive tables:
--   * network_topology_rebuild_attempts    - one row per rebuild attempt of a generation,
--                                            carrying the monotonic fencing token, lease
--                                            owner/expiry/heartbeat, and terminal evidence
--   * network_topology_rebuild_checkpoints - per-stage (snapshot/build/analyze/validate/
--                                            cleanup) progress so a restarted worker can
--                                            resume or safely repeat an idempotent stage
--
-- Rebuild execution, promotion, and multi-node reconciliation policy live in application
-- code (Honua.Routing / Honua.Server); this migration only provisions durable state. The
-- read/solve path never queries these tables.

CREATE TABLE IF NOT EXISTS honua.network_topology_rebuild_attempts (
    dataset_id                TEXT        NOT NULL,
    generation                BIGINT      NOT NULL,
    attempt                   BIGINT      NOT NULL,
    state                     TEXT        NOT NULL,
    operation_id              TEXT        NOT NULL,
    expected_row_version      BIGINT      NOT NULL,
    expected_source_revision  BIGINT      NOT NULL,
    shadow_edge_table         TEXT,
    shadow_vertex_table       TEXT,
    evidence_digest           TEXT,
    failure_code              TEXT,
    owner_id                  TEXT,
    fencing_token             BIGINT      NOT NULL DEFAULT 0,
    lease_expires_at          TIMESTAMPTZ,
    last_heartbeat_at         TIMESTAMPTZ,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at              TIMESTAMPTZ,
    CONSTRAINT network_topology_rebuild_attempts_pk
        PRIMARY KEY (dataset_id, generation, attempt),
    CONSTRAINT network_topology_rebuild_attempts_generation_fk
        FOREIGN KEY (dataset_id, generation)
        REFERENCES honua.network_topology_generations (dataset_id, generation)
        ON DELETE CASCADE,
    CONSTRAINT network_topology_rebuild_attempts_positive_attempt
        CHECK (attempt > 0),
    CONSTRAINT network_topology_rebuild_attempts_nonnegative_token
        CHECK (fencing_token >= 0),
    CONSTRAINT network_topology_rebuild_attempts_valid_state
        CHECK (state IN ('building', 'ready', 'failed')),
    CONSTRAINT network_topology_rebuild_attempts_failure_code_shape
        CHECK (failure_code IS NULL OR failure_code ~ '^[a-z][a-z0-9_.-]{0,63}$'),
    CONSTRAINT network_topology_rebuild_attempts_failure_state
        CHECK ((state = 'failed') = (failure_code IS NOT NULL)),
    CONSTRAINT network_topology_rebuild_attempts_ready_evidence
        CHECK (state <> 'ready' OR (shadow_edge_table IS NOT NULL AND shadow_vertex_table IS NOT NULL AND evidence_digest IS NOT NULL))
);

-- At most one non-terminal (building) attempt per generation: the invariant that fences
-- concurrent rebuild submission at the database layer, independent of the CAS on the
-- owning generation row.
CREATE UNIQUE INDEX IF NOT EXISTS ux_network_topology_rebuild_attempts_active
    ON honua.network_topology_rebuild_attempts (dataset_id, generation)
    WHERE state = 'building';

CREATE INDEX IF NOT EXISTS ix_network_topology_rebuild_attempts_generation
    ON honua.network_topology_rebuild_attempts (dataset_id, generation, attempt DESC);
CREATE INDEX IF NOT EXISTS ix_network_topology_rebuild_attempts_expired_leases
    ON honua.network_topology_rebuild_attempts (lease_expires_at)
    WHERE state = 'building';

CREATE TABLE IF NOT EXISTS honua.network_topology_rebuild_checkpoints (
    dataset_id   TEXT        NOT NULL,
    generation   BIGINT      NOT NULL,
    attempt      BIGINT      NOT NULL,
    stage        TEXT        NOT NULL,
    status       TEXT        NOT NULL,
    detail       TEXT,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT network_topology_rebuild_checkpoints_pk
        PRIMARY KEY (dataset_id, generation, attempt, stage),
    CONSTRAINT network_topology_rebuild_checkpoints_attempt_fk
        FOREIGN KEY (dataset_id, generation, attempt)
        REFERENCES honua.network_topology_rebuild_attempts (dataset_id, generation, attempt)
        ON DELETE CASCADE,
    CONSTRAINT network_topology_rebuild_checkpoints_valid_stage
        CHECK (stage IN ('snapshot', 'build', 'analyze', 'validate', 'cleanup')),
    CONSTRAINT network_topology_rebuild_checkpoints_valid_status
        CHECK (status IN ('pending', 'in_progress', 'completed', 'failed'))
);

COMMENT ON TABLE honua.network_topology_rebuild_attempts IS
    'Durable isolated shadow-topology rebuild attempts (#2718) with a monotonic fencing lease (#2720). Never read by the solve path.';
COMMENT ON TABLE honua.network_topology_rebuild_checkpoints IS
    'Per-stage rebuild progress (#2718) so a restarted worker resumes or safely repeats an idempotent stage.';
COMMENT ON COLUMN honua.network_topology_rebuild_attempts.fencing_token IS
    'Monotonic token incremented on every lease acquisition/takeover. Every checkpoint/completion/failure mutation must present this exact value; a stale value is rejected deterministically.';
