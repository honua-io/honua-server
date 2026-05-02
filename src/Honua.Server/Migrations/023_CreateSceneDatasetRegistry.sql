-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Scene dataset registry persistence for hosted 3D Tiles datasets.
-- Created by ticket #844 to replace the configuration-only ConfigurationSceneDatasetRegistry
-- introduced for the hosted serving slice (#837). The serving path keeps reading the
-- same store via ISceneDatasetRegistry; admin lifecycle goes through ISceneRegistrationService.

CREATE TABLE IF NOT EXISTS honua.scene_datasets (
    dataset_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    id                    VARCHAR(64) NOT NULL,
    name                  VARCHAR(128) NOT NULL,
    description           TEXT,
    asset_root            TEXT NOT NULL,
    tileset_file_name     VARCHAR(64) NOT NULL DEFAULT 'tileset.json',
    dataset_type          VARCHAR(32) NOT NULL DEFAULT 'hosted_tiles',
    extent_xmin           DOUBLE PRECISION,
    extent_ymin           DOUBLE PRECISION,
    extent_xmax           DOUBLE PRECISION,
    extent_ymax           DOUBLE PRECISION,
    crs                   VARCHAR(32),
    cache_max_age_seconds INTEGER NOT NULL DEFAULT 3600,
    cache_no_store        BOOLEAN NOT NULL DEFAULT FALSE,
    edition_gate          VARCHAR(32),
    requires_auth         BOOLEAN NOT NULL DEFAULT FALSE,
    is_public             BOOLEAN NOT NULL DEFAULT TRUE,
    allowed_roles         TEXT[],
    status                VARCHAR(32) NOT NULL DEFAULT 'active',
    validation_message    TEXT,
    revision              INTEGER NOT NULL DEFAULT 1,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by            VARCHAR(255) NOT NULL,
    updated_at            TIMESTAMPTZ,
    CONSTRAINT scene_datasets_id_unique   UNIQUE (id),
    CONSTRAINT scene_datasets_name_unique UNIQUE (name),
    CONSTRAINT scene_datasets_extent_paired CHECK (
        (extent_xmin IS NULL AND extent_ymin IS NULL AND extent_xmax IS NULL AND extent_ymax IS NULL)
        OR (extent_xmin IS NOT NULL AND extent_ymin IS NOT NULL AND extent_xmax IS NOT NULL AND extent_ymax IS NOT NULL)
    ),
    CONSTRAINT scene_datasets_extent_bounds CHECK (
        extent_xmin IS NULL OR (
            extent_xmin BETWEEN -180 AND 180
            AND extent_xmax BETWEEN -180 AND 180
            AND extent_ymin BETWEEN -90 AND 90
            AND extent_ymax BETWEEN -90 AND 90
            AND extent_xmin <= extent_xmax
            AND extent_ymin <= extent_ymax
        )
    ),
    CONSTRAINT scene_datasets_cache_max_age_range CHECK (cache_max_age_seconds BETWEEN 0 AND 86400),
    CONSTRAINT scene_datasets_access_flags_consistent CHECK (is_public <> requires_auth)
);

CREATE INDEX IF NOT EXISTS idx_scene_datasets_status     ON honua.scene_datasets(status);
CREATE INDEX IF NOT EXISTS idx_scene_datasets_created_at ON honua.scene_datasets(created_at);

COMMENT ON TABLE  honua.scene_datasets IS 'Registry of hosted 3D Tiles scene datasets (#844).';
COMMENT ON COLUMN honua.scene_datasets.dataset_id IS 'Stable database primary key — used as the admin-route id.';
COMMENT ON COLUMN honua.scene_datasets.id IS 'URL slug rendered into /scenes/{id}/tileset.json.';
COMMENT ON COLUMN honua.scene_datasets.asset_root IS 'Server-side filesystem directory containing the root tileset document.';
COMMENT ON COLUMN honua.scene_datasets.cache_max_age_seconds IS 'Seconds for the public Cache-Control max-age header (0–86400).';
COMMENT ON COLUMN honua.scene_datasets.allowed_roles IS 'Roles allowed to read a protected dataset (forwarded to AccessPolicy.AllowedRoles).';
COMMENT ON COLUMN honua.scene_datasets.status IS 'Lifecycle state — active | inactive | validation_failed.';
