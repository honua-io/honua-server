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
--
-- Schema note: this targets the MIGRATED `honua.*` schema (the DbUp-managed
-- production schema the docker image runs), which differs from the
-- `tests/seed/base-schema.sql` test-harness schema.

BEGIN;

CREATE EXTENSION IF NOT EXISTS postgis;

-- CNG conformance service. supported_formats advertises the cloud-native export
-- formats alongside JSON/GeoJSON so capability metadata matches runtime
-- behaviour. accessPolicy.allowAnonymous makes the FeatureServer readable
-- without credentials for the conformance run.
INSERT INTO honua.services (
    service_name, description, srid,
    supported_formats, capabilities, service_extent, metadata
)
VALUES (
    'cng',
    'Cloud-Native-Geospatial conformance service (GeoParquet / FlatGeobuf)',
    4326,
    ARRAY['JSON', 'GeoJSON', 'PBF', 'fgb', 'geoparquet'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-180, -90, 180, 90, 4326),
    jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
)
ON CONFLICT (service_name) DO UPDATE SET
    description = EXCLUDED.description,
    srid = EXCLUDED.srid,
    supported_formats = EXCLUDED.supported_formats,
    capabilities = EXCLUDED.capabilities,
    service_extent = EXCLUDED.service_extent,
    metadata = EXCLUDED.metadata,
    updated_at = NOW();

-- CNG Point layer backed by the `features` table (public schema). The legacy
-- feature path keys rows by (layer_id) in `features`, with the object id in the
-- `objectid` column and attributes in the `attributes` jsonb column.
INSERT INTO honua.layers (
    layer_id, layer_name, description,
    table_schema, table_name, primary_key_column, geometry_column,
    geometry_type, srid, extent, default_visibility, enabled, metadata
)
VALUES (
    1000,
    'CNG Features',
    'Seeded point features for cloud-native export conformance (GeoParquet / FlatGeobuf)',
    'public', 'features', 'objectid', 'geometry',
    'Point', 4326,
    ST_MakeEnvelope(-180, -90, 180, 90, 4326),
    true, true,
    jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
)
ON CONFLICT (layer_id) DO UPDATE SET
    layer_name = EXCLUDED.layer_name,
    description = EXCLUDED.description,
    table_schema = EXCLUDED.table_schema,
    table_name = EXCLUDED.table_name,
    primary_key_column = EXCLUDED.primary_key_column,
    geometry_column = EXCLUDED.geometry_column,
    geometry_type = EXCLUDED.geometry_type,
    srid = EXCLUDED.srid,
    extent = EXCLUDED.extent,
    default_visibility = EXCLUDED.default_visibility,
    enabled = EXCLUDED.enabled,
    metadata = EXCLUDED.metadata;

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
) AS seed(lon, lat, name, category, population, ratio, active, observed_at)
WHERE NOT EXISTS (SELECT 1 FROM features WHERE layer_id = 1000);

-- The runtime catalog is sourced from the active Metadata v2 graph, not the
-- legacy service/layer tables above. Publish the same store-backed resource in
-- both environments the container may resolve during Test startup.
DO $cng_metadata$
DECLARE
    target_environment text;
    target_revision bigint;
    snapshot_document jsonb;
    snapshot_etag text;
    ready_status jsonb := jsonb_build_object('lifecycle', 'active', 'state', 'ready');
BEGIN
    FOREACH target_environment IN ARRAY ARRAY['default', 'Test']
    LOOP
        SELECT COALESCE(MAX(revision), 0) + 1
          INTO target_revision
          FROM honua.metadata_v2_snapshots
         WHERE environment = target_environment;

        snapshot_document := jsonb_build_object(
            'schemaVersion', '2.0.0-alpha.1',
            'apiVersion', 'metadata.honua.io/v2alpha1',
            'revision', target_revision,
            'environment', target_environment,
            'generatedAt', NOW(),
            'namespaces', jsonb_build_array('cng'),
            'metadata', jsonb_build_object(
                'id', 'cng-conformance-seed',
                'name', 'cng-conformance-seed',
                'title', 'CNG conformance seed'),
            'catalogs', '[]'::jsonb,
            'resources', jsonb_build_array(jsonb_build_object(
                'metadata', jsonb_build_object(
                    'id', 'res-cng-1000',
                    'name', 'cng-features',
                    'title', 'CNG Features',
                    'description', 'Cloud-native export conformance features'),
                'type', 'feature-dataset',
                'storageBindingIds', jsonb_build_array('storage-cng-1000'),
                'primaryStorageBindingId', 'storage-cng-1000',
                'policyIds', '[]'::jsonb,
                'schemaFields', jsonb_build_array(
                    jsonb_build_object('name', 'objectid', 'type', 'integer', 'nullable', false,
                        'semanticRoles', jsonb_build_array('id.primary')),
                    jsonb_build_object('name', 'name', 'type', 'string', 'nullable', true),
                    jsonb_build_object('name', 'category', 'type', 'string', 'nullable', true),
                    jsonb_build_object('name', 'population', 'type', 'integer', 'nullable', true),
                    jsonb_build_object('name', 'ratio', 'type', 'double', 'nullable', true),
                    jsonb_build_object('name', 'active', 'type', 'boolean', 'nullable', true),
                    jsonb_build_object('name', 'observed_at', 'type', 'datetime', 'nullable', true),
                    jsonb_build_object('name', 'geometry', 'type', 'geometry', 'nullable', true,
                        'semanticRoles', jsonb_build_array('geometry.primary'))),
                'relationships', '[]'::jsonb,
                'styleResourceIds', '[]'::jsonb,
                'spatial', jsonb_build_object(
                    'spatialReference', jsonb_build_object(
                        'srid', 4326, 'crs', 'EPSG:4326', 'isGeographic', true),
                    'geometryType', 'point',
                    'bbox', jsonb_build_object(
                        'west', -180, 'south', -90, 'east', 180, 'north', 90),
                    'primaryGeometryField', 'geometry'),
                'temporal', jsonb_build_object('startTimeField', 'observed_at'),
                'accessPolicy', jsonb_build_object('allowAnonymous', true),
                'status', ready_status,
                'extensions', '{}'::jsonb)),
            'connections', jsonb_build_array(jsonb_build_object(
                'metadata', jsonb_build_object('id', 'conn-cng-postgres', 'name', 'cng-postgres'),
                'type', 'managed',
                'provider', 'postgres',
                'status', ready_status)),
            'storageBindings', jsonb_build_array(jsonb_build_object(
                'metadata', jsonb_build_object('id', 'storage-cng-1000', 'name', 'storage-cng-1000'),
                'resourceId', 'res-cng-1000',
                'connectionId', NULL,
                'storageType', 'relational-table',
                'locator', 'public.features',
                'storageLayerId', 1000,
                'capabilities', jsonb_build_array(
                    'query', 'filter', 'sort', 'aggregate', 'edit', 'transactions',
                    'render', 'tile', 'search'),
                'options', jsonb_build_object(
                    'schemaName', 'public',
                    'tableName', 'features',
                    'primaryKeyColumn', 'objectid',
                    'attributesColumn', 'attributes',
                    'geometryColumn', 'geometry',
                    'layerDiscriminatorColumn', 'layer_id'),
                'status', ready_status,
                'extensions', '{}'::jsonb)),
            'services', jsonb_build_array(
                jsonb_build_object(
                    'metadata', jsonb_build_object(
                        'id', 'svc-cng-feature', 'name', 'cng', 'title', 'CNG Feature Service'),
                    'serviceType', 'esri-feature-service',
                    'publicationIds', jsonb_build_array('pub-cng-feature-1000'),
                    'protocols', jsonb_build_array('FeatureServer'),
                    'enabledProtocols', jsonb_build_array('FeatureServer'),
                    'options', '{}'::jsonb,
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'status', ready_status,
                    'extensions', '{}'::jsonb),
                jsonb_build_object(
                    'metadata', jsonb_build_object(
                        'id', 'svc-cng-stac', 'name', 'cng-stac', 'title', 'CNG STAC Catalog'),
                    'serviceType', 'stac-api',
                    'route', '/stac',
                    'publicationIds', jsonb_build_array('pub-cng-stac-1000'),
                    'protocols', jsonb_build_array('Stac'),
                    'enabledProtocols', jsonb_build_array('Stac'),
                    'options', '{}'::jsonb,
                    'accessPolicy', jsonb_build_object('allowAnonymous', true),
                    'status', ready_status,
                    'extensions', '{}'::jsonb)),
            'publications', jsonb_build_array(
                jsonb_build_object(
                    'metadata', jsonb_build_object(
                        'id', 'pub-cng-feature-1000', 'name', '1000', 'title', 'CNG Features'),
                    'resourceId', 'res-cng-1000',
                    'serviceId', 'svc-cng-feature',
                    'storageBindingId', 'storage-cng-1000',
                    'publicationType', 'esri-feature-layer',
                    'path', '1000',
                    'layerIndex', 1000,
                    'serviceLocalId', '1000',
                    'supportedFormats', jsonb_build_array('json', 'geojson', 'pbf', 'fgb', 'parquet'),
                    'capabilities', jsonb_build_array('Query', 'Extract'),
                    'status', ready_status,
                    'options', '{}'::jsonb,
                    'extensions', '{}'::jsonb),
                jsonb_build_object(
                    'metadata', jsonb_build_object(
                        'id', 'pub-cng-stac-1000', 'name', 'cng-features', 'title', 'CNG Features'),
                    'resourceId', 'res-cng-1000',
                    'serviceId', 'svc-cng-stac',
                    'storageBindingId', 'storage-cng-1000',
                    'publicationType', 'stac-collection',
                    'path', 'cng-features',
                    'layerIndex', 1000,
                    'serviceLocalId', 'cng-features',
                    'supportedFormats', '[]'::jsonb,
                    'capabilities', jsonb_build_array('Query'),
                    'status', ready_status,
                    'options', '{}'::jsonb,
                    'extensions', '{}'::jsonb)),
            'projectionProfiles', '[]'::jsonb,
            'policies', '[]'::jsonb,
            'roles', '[]'::jsonb,
            'extensionPoints', '[]'::jsonb);

        snapshot_etag := '"' || md5(snapshot_document::text) || '"';
        INSERT INTO honua.metadata_v2_snapshots (
            environment, revision, schema_version, api_version, document, etag, generated_at)
        VALUES (
            target_environment, target_revision, '2.0.0-alpha.1',
            'metadata.honua.io/v2alpha1', snapshot_document, snapshot_etag, NOW());

        INSERT INTO honua.metadata_v2_current (environment, revision, etag, activated_at)
        VALUES (target_environment, target_revision, snapshot_etag, NOW())
        ON CONFLICT (environment) DO UPDATE SET
            revision = EXCLUDED.revision,
            etag = EXCLUDED.etag,
            activated_at = EXCLUDED.activated_at;
    END LOOP;
END
$cng_metadata$;

COMMIT;
