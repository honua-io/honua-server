-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 084_CreateNetworkTopologyGenerations.sql
-- Foundation for #2715 / umbrella #2656. A generation is an immutable routing
-- topology candidate. Content editing, rebuilding, promotion, rollback, and worker
-- coordination are deliberately not implemented by this migration.
--
-- Rolling-upgrade safety: the existing honua.network_datasets mapping remains the
-- solve-path source of truth. Old and new binaries therefore resolve the same edge
-- and vertex tables while this additive metadata table is deployed. A later,
-- separately gated promotion change will switch resolution atomically.
--
-- Re-application safety: DDL uses IF NOT EXISTS and backfill inserts only when a
-- dataset has no active generation. The migration can be safely retried without
-- allocating another generation or changing the live mapping.

CREATE TABLE IF NOT EXISTS honua.network_topology_generations (
    dataset_id       TEXT        NOT NULL,
    generation       BIGINT      NOT NULL,
    source_revision  BIGINT      NOT NULL DEFAULT 0,
    state             TEXT        NOT NULL,
    row_version       BIGINT      NOT NULL DEFAULT 1,
    edge_table        TEXT        NOT NULL,
    vertex_table      TEXT        NOT NULL,
    srid              INTEGER     NOT NULL,
    failure_code      TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    activated_at      TIMESTAMPTZ,
    CONSTRAINT network_topology_generations_pk
        PRIMARY KEY (dataset_id, generation),
    CONSTRAINT network_topology_generations_dataset_fk
        FOREIGN KEY (dataset_id) REFERENCES honua.network_datasets (id) ON DELETE CASCADE,
    CONSTRAINT network_topology_generations_positive_generation
        CHECK (generation > 0),
    CONSTRAINT network_topology_generations_nonnegative_source_revision
        CHECK (source_revision >= 0),
    CONSTRAINT network_topology_generations_positive_row_version
        CHECK (row_version > 0),
    CONSTRAINT network_topology_generations_valid_state
        CHECK (state IN ('draft', 'dirty', 'building', 'ready', 'active', 'failed', 'retired')),
    CONSTRAINT network_topology_generations_failure_code_shape
        CHECK (failure_code IS NULL OR failure_code ~ '^[a-z][a-z0-9_.-]{0,63}$'),
    CONSTRAINT network_topology_generations_failure_state
        CHECK ((state = 'failed') = (failure_code IS NOT NULL)),
    CONSTRAINT network_topology_generations_activation_state
        CHECK (state <> 'active' OR activated_at IS NOT NULL)
);

-- At most one active solve target can exist for a dataset. The backfill below gives
-- every existing registry row one, making the invariant exactly one at deployment.
CREATE UNIQUE INDEX IF NOT EXISTS ux_network_topology_generations_active_dataset
    ON honua.network_topology_generations (dataset_id)
    WHERE state = 'active';

CREATE INDEX IF NOT EXISTS ix_network_topology_generations_dataset_state
    ON honua.network_topology_generations (dataset_id, state, generation DESC);

-- The database owns the registration invariant during rolling upgrades. An old
-- replica can continue using its pre-084 INSERT statement, but this trigger runs in
-- that same statement transaction and creates the initial active generation before
-- the registry row can commit. The new store validates the result before its own
-- transaction commits instead of assuming every replica runs new application code.
CREATE OR REPLACE FUNCTION honua.seed_initial_network_topology_generation()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    next_generation BIGINT;
BEGIN
    SELECT GREATEST(
        NEW.topology_version::bigint,
        COALESCE(MAX(existing.generation) + 1, 1))
    INTO next_generation
    FROM honua.network_topology_generations AS existing
    WHERE existing.dataset_id = NEW.id;

    INSERT INTO honua.network_topology_generations (
        dataset_id,
        generation,
        source_revision,
        state,
        row_version,
        edge_table,
        vertex_table,
        srid,
        created_at,
        updated_at,
        activated_at)
    SELECT
        NEW.id,
        next_generation,
        0,
        'active',
        1,
        NEW.edge_table,
        NEW.vertex_table,
        NEW.srid,
        NEW.created_at,
        NEW.updated_at,
        now()
    WHERE NOT EXISTS (
        SELECT 1
        FROM honua.network_topology_generations AS active
        WHERE active.dataset_id = NEW.id
          AND active.state = 'active')
    ON CONFLICT (dataset_id, generation) DO NOTHING;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS network_datasets_seed_initial_generation
    ON honua.network_datasets;
CREATE TRIGGER network_datasets_seed_initial_generation
    AFTER INSERT ON honua.network_datasets
    FOR EACH ROW
    EXECUTE FUNCTION honua.seed_initial_network_topology_generation();

-- A rolled-back or not-yet-upgraded replica can still use the legacy registry PUT
-- surface to change the live solve mapping. Preserve immutable generation history by
-- retiring the old active generation and recording the replacement in the same UPDATE
-- transaction. New topology writers must use the generation lifecycle instead; this
-- trigger is the mixed-version compatibility boundary while the legacy registry remains
-- the solve-path source of truth.
CREATE OR REPLACE FUNCTION honua.track_legacy_network_topology_mapping_update()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    next_generation BIGINT;
    next_source_revision BIGINT;
    retired_count INTEGER;
BEGIN
    IF OLD.edge_table IS NOT DISTINCT FROM NEW.edge_table
       AND OLD.vertex_table IS NOT DISTINCT FROM NEW.vertex_table
       AND OLD.srid IS NOT DISTINCT FROM NEW.srid THEN
        RETURN NEW;
    END IF;

    UPDATE honua.network_topology_generations
    SET state = 'retired',
        row_version = row_version + 1,
        updated_at = NEW.updated_at
    WHERE dataset_id = NEW.id
      AND state = 'active'
    RETURNING source_revision + 1 INTO next_source_revision;
    GET DIAGNOSTICS retired_count = ROW_COUNT;

    IF retired_count <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'network topology active generation invariant violated';
    END IF;

    SELECT GREATEST(
        NEW.topology_version::bigint,
        COALESCE(MAX(existing.generation) + 1, 1))
    INTO next_generation
    FROM honua.network_topology_generations AS existing
    WHERE existing.dataset_id = NEW.id;

    INSERT INTO honua.network_topology_generations (
        dataset_id,
        generation,
        source_revision,
        state,
        row_version,
        edge_table,
        vertex_table,
        srid,
        created_at,
        updated_at,
        activated_at)
    VALUES (
        NEW.id,
        next_generation,
        next_source_revision,
        'active',
        1,
        NEW.edge_table,
        NEW.vertex_table,
        NEW.srid,
        NEW.updated_at,
        NEW.updated_at,
        now());

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS network_datasets_track_legacy_mapping_update
    ON honua.network_datasets;
CREATE TRIGGER network_datasets_track_legacy_mapping_update
    AFTER UPDATE OF edge_table, vertex_table, srid ON honua.network_datasets
    FOR EACH ROW
    EXECUTE FUNCTION honua.track_legacy_network_topology_mapping_update();

-- Install the compatibility triggers before taking the backfill snapshot. Trigger DDL
-- holds its table lock until this transactional migration commits: writes that began
-- earlier finish before trigger installation and are visible here, while later writes
-- wait and execute with the trigger active. This ordering prevents a registration from
-- landing between backfill and trigger installation with zero active generations.
INSERT INTO honua.network_topology_generations (
    dataset_id,
    generation,
    source_revision,
    state,
    row_version,
    edge_table,
    vertex_table,
    srid,
    created_at,
    updated_at,
    activated_at)
SELECT
    dataset.id,
    GREATEST(dataset.topology_version::bigint, allocated.next_generation),
    0,
    'active',
    1,
    dataset.edge_table,
    dataset.vertex_table,
    dataset.srid,
    dataset.created_at,
    dataset.updated_at,
    now()
FROM honua.network_datasets AS dataset
JOIN LATERAL (
    SELECT COALESCE(MAX(existing.generation) + 1, 1) AS next_generation
    FROM honua.network_topology_generations AS existing
    WHERE existing.dataset_id = dataset.id
) AS allocated ON true
WHERE NOT EXISTS (
    SELECT 1
    FROM honua.network_topology_generations AS existing
    WHERE existing.dataset_id = dataset.id
      AND existing.state = 'active')
ON CONFLICT (dataset_id, generation) DO NOTHING;

COMMENT ON TABLE honua.network_topology_generations IS
    'Immutable routing topology generation metadata. Non-active generations are never solve targets.';
COMMENT ON COLUMN honua.network_topology_generations.row_version IS
    'Compare-and-swap version. Writers must match and increment this value atomically.';
COMMENT ON COLUMN honua.network_topology_generations.failure_code IS
    'Sanitized stable code only; never SQL, table names, geometry, or exception text.';
