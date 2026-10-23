-- WCS fixture data; the pinned server owns all catalog and raster schema.

BEGIN;

TRUNCATE honua.service_layers RESTART IDENTITY CASCADE;
TRUNCATE honua.layer_fields RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_tiles RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_statistics RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_data RESTART IDENTITY CASCADE;
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE honua.services RESTART IDENTITY CASCADE;

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
    'cite',
    'Seeded WCS 2.0.1 service for OGC CITE conformance tests',
    4326,
    ARRAY['image/tiff', 'image/png', 'image/jpeg'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-122.52, 37.68, -122.32, 37.86, 4326),
    '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb
);

INSERT INTO honua.layers (
    layer_id,
    layer_name,
    description,
    table_schema,
    table_name,
    primary_key_column,
    geometry_column,
    geometry_type,
    srid,
    extent,
    default_visibility,
    metadata,
    enabled
)
VALUES
    (
        101,
        'cite-dem-west',
        'Deterministic 16x16 rectified grid coverage for WCS CITE',
        'honua',
        'raster_data',
        'id',
        NULL,
        'None',
        4326,
        ST_MakeEnvelope(-122.52, 37.70, -122.36, 37.86, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb,
        TRUE
    ),
    (
        102,
        'cite-dem-east',
        'Second deterministic rectified grid coverage for WCS CITE',
        'honua',
        'raster_data',
        'id',
        NULL,
        'None',
        4326,
        ST_MakeEnvelope(-122.44, 37.68, -122.32, 37.80, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb,
        TRUE
    );

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('cite', 101, 0),
    ('cite', 102, 1);

INSERT INTO honua.layer_fields (
    layer_id,
    field_name,
    field_type,
    field_order,
    nullable,
    description
)
VALUES
    (101, 'id', 'Integer', 0, FALSE, 'Raster identifier'),
    (102, 'id', 'Integer', 0, FALSE, 'Raster identifier');

INSERT INTO honua.raster_data (
    layer_id,
    name,
    description,
    raster,
    acquisition_date,
    created_at
)
VALUES
    (
        101,
        'cite-dem-west-primary',
        'Constant elevation fixture for WCS CITE coverage 101',
        ST_AddBand(
            ST_MakeEmptyRaster(16, 16, -122.52, 37.86, 0.01, -0.01, 0, 0, 4326),
            '32BF'::text,
            125.0,
            -9999.0
        ),
        '2024-01-01T00:00:00Z',
        '2024-01-01T00:00:00Z'
    ),
    (
        102,
        'cite-dem-east-primary',
        'Constant elevation fixture for WCS CITE coverage 102',
        ST_AddBand(
            ST_MakeEmptyRaster(24, 24, -122.44, 37.80, 0.005, -0.005, 0, 0, 4326),
            '32BF'::text,
            80.0,
            -9999.0
        ),
        '2024-01-02T00:00:00Z',
        '2024-01-02T00:00:00Z'
    );

INSERT INTO honua.raster_statistics (
    raster_data_id,
    band_number,
    min_value,
    max_value,
    mean_value,
    std_dev,
    valid_pixel_count,
    nodata_pixel_count
)
SELECT
    id,
    1,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    0.0,
    width * height,
    0
FROM honua.raster_data
ON CONFLICT (raster_data_id, band_number) DO UPDATE SET
    min_value = EXCLUDED.min_value,
    max_value = EXCLUDED.max_value,
    mean_value = EXCLUDED.mean_value,
    std_dev = EXCLUDED.std_dev,
    valid_pixel_count = EXCLUDED.valid_pixel_count,
    nodata_pixel_count = EXCLUDED.nodata_pixel_count,
    computed_at = NOW();

COMMIT;
