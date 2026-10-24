-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Managed enrichment-dataset catalog (#2280, parent #374). Designates an existing
-- managed layer as a reusable enrichment source (boundary / demographic / POI) so
-- the enrichment surface (GET /api/enrich/datasets, POST /api/enrich) can resolve a
-- stable dataset slug instead of a bare numeric layer id, and so provenance,
-- attribution, license, and the minimum edition tier travel with the dataset.
--
-- Additive and optional: empty by default; rows are created by the admin
-- registration endpoint (or the optional bundled-dataset seed). The existing
-- spatial-join surface is unchanged. join_attributes is stored as a comma-separated
-- TEXT column (a small, order-stable field list) so the store row mapper stays a
-- plain ordinal read.

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.enrichment_datasets (
    id                TEXT             NOT NULL PRIMARY KEY,
    title             TEXT             NOT NULL,
    category          TEXT             NOT NULL DEFAULT 'boundary',
    layer_id          INTEGER          NOT NULL,
    geometry_type     TEXT,
    join_attributes   TEXT,
    default_predicate TEXT             NOT NULL DEFAULT 'intersects',
    distance_meters   DOUBLE PRECISION,
    provenance        TEXT,
    attribution       TEXT,
    license           TEXT,
    minimum_edition   TEXT             NOT NULL DEFAULT 'Pro',
    created_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ      NOT NULL DEFAULT now(),
    created_by        TEXT,
    updated_by        TEXT,
    CONSTRAINT enrichment_datasets_valid_category
        CHECK (category IN ('boundary', 'demographic', 'poi')),
    CONSTRAINT enrichment_datasets_valid_predicate
        CHECK (default_predicate IN ('intersects', 'contains', 'within', 'dwithin'))
);

CREATE INDEX IF NOT EXISTS idx_enrichment_datasets_category
    ON honua.enrichment_datasets (category);

CREATE INDEX IF NOT EXISTS idx_enrichment_datasets_layer_id
    ON honua.enrichment_datasets (layer_id);
