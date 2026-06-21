-- Seed data for the Cloud-Native-Geospatial (CNG) conformance lane.
--
-- Creates a Postgres-backed FeatureServer ('cng') whose single Point layer is
-- served from the `features` table. Because the layer resolves to the canonical
-- PostgreSQL feature store (PostgresFeatureStoreRefactored), which implements
-- IFlatGeobufFeatureStore / IGeobufFeatureStore and the shared GeoParquet
-- encoder, the FeatureServer query endpoint can emit `f=parquet` (GeoParquet
-- 1.1.0) and `f=fgb` (FlatGeobuf) for the seeded features. The in-memory test
-- store used by unit fixtures does NOT implement those markers, which is why the
-- default `test`/`browser_compat` services return HTTP 400 for those formats;
-- this lane provisions a store-backed service so the cloud-native output paths
-- are exercisable end to end.

BEGIN;

CREATE EXTENSION IF NOT EXISTS postgis;

-- CNG conformance service. supported_formats advertises the cloud-native export
-- formats alongside JSON/GeoJSON so capability metadata matches runtime behaviour.
INSERT INTO honua.services (
    service_name, description, srid, max_record_count,
    supported_formats, capabilities, service_extent
)
VALUES (
    'cng',
    'Cloud-Native-Geospatial conformance service (GeoParquet / FlatGeobuf)',
    4326,
    5000,
    ARRAY['JSON', 'GeoJSON', 'PBF', 'fgb', 'geoparquet'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-180, -90, 180, 90, 4326)
)
ON CONFLICT (service_name) DO UPDATE SET
    description = EXCLUDED.description,
    srid = EXCLUDED.srid,
    max_record_count = EXCLUDED.max_record_count,
    supported_formats = EXCLUDED.supported_formats,
    capabilities = EXCLUDED.capabilities,
    service_extent = EXCLUDED.service_extent,
    updated_at = NOW();

UPDATE honua.services
SET metadata = jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
WHERE service_name = 'cng';

-- CNG Point layer backed by the `features` table.
INSERT INTO honua.layers (
    layer_id, layer_name, description, table_name,
    geometry_type, srid, extent, default_visibility
)
VALUES (
    1000,
    'CNG Features',
    'Seeded point features for cloud-native export conformance (GeoParquet / FlatGeobuf)',
    'features',
    'Point',
    4326,
    ST_MakeEnvelope(-180, -90, 180, 90, 4326),
    true
)
ON CONFLICT (layer_id) DO UPDATE SET
    layer_name = EXCLUDED.layer_name,
    description = EXCLUDED.description,
    table_name = EXCLUDED.table_name,
    geometry_type = EXCLUDED.geometry_type,
    srid = EXCLUDED.srid,
    extent = EXCLUDED.extent,
    default_visibility = EXCLUDED.default_visibility;

UPDATE honua.layers
SET metadata = jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
WHERE layer_id = 1000;

-- Layer fields cover the common attribute scalar types so the GeoParquet schema
-- and FlatGeobuf property descriptors exercise integer, double, boolean, string
-- and timestamp column encodings.
INSERT INTO honua.layer_fields (
    layer_id, field_name, field_type, field_order,
    max_length, nullable, default_value, description
)
VALUES
    (1000, 'objectid', 'Integer', 0, NULL, false, NULL, 'Object ID'),
    (1000, 'name', 'String', 1, 255, true, NULL, 'Name'),
    (1000, 'category', 'String', 2, 64, true, NULL, 'Category'),
    (1000, 'population', 'Integer', 3, NULL, true, NULL, 'Population'),
    (1000, 'ratio', 'Double', 4, NULL, true, NULL, 'Ratio'),
    (1000, 'active', 'Boolean', 5, NULL, true, NULL, 'Active flag'),
    (1000, 'observed_at', 'DateTime', 6, NULL, true, NULL, 'Observed timestamp')
ON CONFLICT (layer_id, field_name) DO NOTHING;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('cng', 1000, 0)
ON CONFLICT (service_name, layer_id) DO NOTHING;

-- Deterministic features spanning antimeridian / equator / poles so the export
-- validators see a representative coordinate envelope. Uses real attribute values
-- for every declared column.
INSERT INTO features (layer_id, geometry, attributes)
SELECT 1000,
       ST_SetSRID(ST_MakePoint(lon, lat), 4326),
       jsonb_build_object(
           'name', name,
           'category', category,
           'population', population,
           'ratio', ratio,
           'active', active,
           'observed_at', observed_at)
FROM (
    VALUES
        (-122.4194, 37.7749, 'Harbor City',     'city',      1000000, 0.91, true,  '2026-01-02T03:04:05Z'),
        (-122.2711, 37.8044, 'Baytown',         'city',       430000, 0.42, true,  '2026-02-11T12:00:00Z'),
        (   0.0000, 51.4779, 'Meridian Marker', 'reference',       0, 0.00, false, '2026-03-21T00:00:00Z'),
        ( -75.0000,  0.0000, 'Equator Station', 'reference',       0, 0.50, true,  '2026-04-01T06:30:00Z'),
        ( 179.5000,  0.5000, 'Dateline Post',   'reference',       0, 0.75, false, '2026-05-09T18:45:00Z'),
        (   0.0000, 86.0000, 'Polar Outpost',   'reference',       0, 0.10, true,  '2026-06-15T09:15:00Z')
) AS seed(lon, lat, name, category, population, ratio, active, observed_at);

COMMIT;
