-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

BEGIN;

DELETE FROM honua.service_layers
WHERE service_name = 'stac_ops_demo';

DELETE FROM honua.layer_fields
WHERE layer_id IN (68810, 68820, 68830);

DELETE FROM honua.layers
WHERE layer_id IN (68810, 68820, 68830);

DELETE FROM honua.services
WHERE service_name = 'stac_ops_demo';

DELETE FROM features
WHERE layer_id IN (68810, 68820, 68830);

INSERT INTO honua.services (
    service_name,
    description,
    srid,
    supported_formats,
    capabilities,
    service_extent,
    metadata
)
VALUES (
    'stac_ops_demo',
    'Deterministic STAC operations demo service',
    4326,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-158.35, 21.15, -157.55, 21.75, 4326),
    jsonb_build_object(
        'accessPolicy', jsonb_build_object('allowAnonymous', true)
    )
);

INSERT INTO honua.layers (
    layer_id,
    layer_name,
    description,
    table_schema,
    table_name,
    geometry_type,
    srid,
    extent,
    default_visibility,
    enabled,
    metadata
)
VALUES
    (
        68810,
        'Sentinel Reef Watch',
        'Healthy STAC collection with declared EO, Projection, and View extensions.',
        'public',
        'features',
        'Point',
        4326,
        ST_MakeEnvelope(-158.05, 21.20, -157.70, 21.38, 4326),
        true,
        true,
        jsonb_build_object(
            'timeInfo', jsonb_build_object('startTimeField', 'observed_at'),
            'stac', jsonb_build_object(
                'license', 'CC-BY-4.0',
                'keywords', jsonb_build_array('imagery', 'eo', 'projection', 'ops-demo'),
                'extensions', jsonb_build_array(
                    'https://stac-extensions.github.io/eo/v1.1.0/schema.json',
                    'https://stac-extensions.github.io/projection/v1.1.0/schema.json',
                    'https://stac-extensions.github.io/view/v1.0.0/schema.json'
                )
            )
        )
    ),
    (
        68820,
        'Harbor Drift Watch',
        'Warning STAC collection with observed extension fields but incomplete declarations.',
        'public',
        'features',
        'Point',
        4326,
        ST_MakeEnvelope(-157.98, 21.28, -157.62, 21.58, 4326),
        true,
        true,
        jsonb_build_object(
            'stac', jsonb_build_object(
                'license', 'proprietary',
                'keywords', jsonb_build_array('imagery', 'warning-state', 'ops-demo'),
                'extensions', jsonb_build_array(
                    'https://stac-extensions.github.io/eo/v1.1.0/schema.json'
                )
            )
        )
    );

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('stac_ops_demo', 68810, 0),
    ('stac_ops_demo', 68820, 1);

INSERT INTO honua.layer_fields (
    layer_id,
    field_name,
    field_type,
    field_order,
    max_length,
    nullable,
    description
)
VALUES
    (68810, 'objectid', 'Integer', 0, NULL, false, 'Object ID'),
    (68810, 'name', 'String', 1, 255, false, 'Observation name'),
    (68810, 'observed_at', 'DateTime', 2, NULL, false, 'Observation timestamp'),
    (68810, 'quality_score', 'Integer', 3, NULL, false, 'Compatibility probe score'),
    (68810, 'eo:cloud_cover', 'Double', 4, NULL, true, 'EO cloud cover'),
    (68810, 'proj:epsg', 'Integer', 5, NULL, true, 'Projection EPSG'),
    (68810, 'view:sun_azimuth', 'Double', 6, NULL, true, 'View sun azimuth'),
    (68810, 'platform', 'String', 7, 64, true, 'Platform'),
    (68810, 'shape', 'Geometry', 8, NULL, true, 'Geometry'),
    (68820, 'objectid', 'Integer', 0, NULL, false, 'Object ID'),
    (68820, 'name', 'String', 1, 255, false, 'Observation name'),
    (68820, 'observed_at', 'DateTime', 2, NULL, true, 'Observation timestamp'),
    (68820, 'quality_score', 'Integer', 3, NULL, false, 'Compatibility probe score'),
    (68820, 'eo:cloud_cover', 'Double', 4, NULL, true, 'EO cloud cover'),
    (68820, 'proj:epsg', 'Integer', 5, NULL, true, 'Projection EPSG'),
    (68820, 'view:sun_azimuth', 'Double', 6, NULL, true, 'View sun azimuth'),
    (68820, 'platform', 'String', 7, 64, true, 'Platform'),
    (68820, 'shape', 'Geometry', 8, NULL, true, 'Geometry');

INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    (
        6881001,
        68810,
        ST_SetSRID(ST_Point(-157.9250, 21.2850), 4326),
        jsonb_build_object(
            'name', 'Sentinel-01',
            'observed_at', '2026-03-01T18:00:00Z',
            'quality_score', 96,
            'eo:cloud_cover', 6.5,
            'proj:epsg', 4326,
            'view:sun_azimuth', 144.0,
            'platform', 'sentinel-2a'
        )
    ),
    (
        6881002,
        68810,
        ST_SetSRID(ST_Point(-157.8800, 21.3100), 4326),
        jsonb_build_object(
            'name', 'Sentinel-02',
            'observed_at', '2026-03-02T18:00:00Z',
            'quality_score', 92,
            'eo:cloud_cover', 11.2,
            'proj:epsg', 4326,
            'view:sun_azimuth', 146.4,
            'platform', 'sentinel-2b'
        )
    ),
    (
        6881003,
        68810,
        ST_SetSRID(ST_Point(-157.8350, 21.3320), 4326),
        jsonb_build_object(
            'name', 'Sentinel-03',
            'observed_at', '2026-03-03T18:00:00Z',
            'quality_score', 88,
            'eo:cloud_cover', 14.8,
            'proj:epsg', 4326,
            'view:sun_azimuth', 149.9,
            'platform', 'sentinel-2a'
        )
    ),
    (
        6881004,
        68810,
        ST_SetSRID(ST_Point(-157.7900, 21.3550), 4326),
        jsonb_build_object(
            'name', 'Sentinel-04',
            'observed_at', '2026-03-04T18:00:00Z',
            'quality_score', 83,
            'eo:cloud_cover', 19.4,
            'proj:epsg', 4326,
            'view:sun_azimuth', 153.3,
            'platform', 'sentinel-2b'
        )
    ),
    (
        6882001,
        68820,
        ST_SetSRID(ST_Point(-157.9550, 21.3900), 4326),
        jsonb_build_object(
            'name', 'Harbor-01',
            'observed_at', '2026-03-01T19:00:00Z',
            'quality_score', 78,
            'eo:cloud_cover', 17.0,
            'proj:epsg', 4326,
            'view:sun_azimuth', 132.0,
            'platform', 'drone-alpha'
        )
    ),
    (
        6882002,
        68820,
        ST_SetSRID(ST_Point(-157.8650, 21.4550), 4326),
        jsonb_build_object(
            'name', 'Harbor-02',
            'observed_at', '2026-03-02T19:00:00Z',
            'quality_score', 64,
            'eo:cloud_cover', 23.5,
            'proj:epsg', 4326,
            'view:sun_azimuth', 128.4,
            'platform', 'drone-alpha'
        )
    ),
    (
        6882003,
        68820,
        ST_SetSRID(ST_Point(-157.7750, 21.5200), 4326),
        jsonb_build_object(
            'name', 'Harbor-03',
            'observed_at', '2026-03-03T19:00:00Z',
            'quality_score', 59,
            'eo:cloud_cover', 28.9,
            'proj:epsg', 4326,
            'view:sun_azimuth', 126.7,
            'platform', 'drone-beta'
        )
    );

COMMIT;
