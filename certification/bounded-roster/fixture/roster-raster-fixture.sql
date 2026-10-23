-- Bounded-roster raster fixture (honua-server#3434).
--
-- Applied after tests/seed/client-compat-v1.sql, the client-compat YAML seeds and
-- docker/cng/seed.sql, and after honua.seed_metadata_v2_compat_snapshot() has
-- compiled those V1 rows. It adds the raster publications the governed raster
-- cells need and that no existing fixture provides:
--
--   5000 roster_cog            cloud COG target (registered by apply_fixture.py)
--   5001 roster_cog_protected  access-controlled mirror of 5000
--   5100 roster_dem            PostGIS DEM served as Terrain-RGB and elevation
--   5101 roster_dem_protected  access-controlled mirror of 5100
--   5200 roster_zarr           Zarr datacube (registered by apply_fixture.py)
--   5201 roster_zarr_protected access-controlled mirror of 5200
--
-- The compat compiler gives every V1 layer five publications (feature, map,
-- image, OGC, STAC) sharing one layer index. Cloud COG and Zarr registrations
-- bind by layer index and fail closed on an ambiguous index
-- (CogPublicationBinding.Resolve), so these layers are published here with a
-- single image publication each, on a service whose enabled protocols are the
-- raster surfaces under test. Each environment's active snapshot is extended in
-- place and its etag rotated so the running candidate reloads it.

BEGIN;

INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
SELECT name, 'Bounded-roster raster fixture', 4326, ARRAY['image/tiff', 'image/png'], ARRAY['Query', 'Extract'],
       ST_MakeEnvelope(-122.46, 37.72, -122.38, 37.80, 4326),
       jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', anonymous))
FROM (VALUES ('roster_cog', true), ('roster_cog_protected', false), ('roster_dem', true),
             ('roster_dem_protected', false), ('roster_zarr', true), ('roster_zarr_protected', false)) AS s(name, anonymous)
ON CONFLICT (service_name) DO UPDATE SET metadata = EXCLUDED.metadata, updated_at = NOW();

INSERT INTO honua.layers (layer_id, layer_name, description, table_schema, table_name, primary_key_column,
                          geometry_column, geometry_type, srid, extent, default_visibility, metadata, enabled)
SELECT id, title, 'Bounded-roster raster fixture layer', 'honua', 'raster_data', 'id', NULL, 'None', 4326,
       ST_MakeEnvelope(-122.46, 37.72, -122.38, 37.80, 4326), TRUE,
       jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', anonymous)), TRUE
FROM (VALUES (5000, 'Roster COG', true), (5001, 'Roster COG (protected)', false),
             (5100, 'Roster DEM', true), (5101, 'Roster DEM (protected)', false),
             (5200, 'Roster Zarr', true), (5201, 'Roster Zarr (protected)', false)) AS l(id, title, anonymous)
ON CONFLICT (layer_id) DO UPDATE SET layer_name = EXCLUDED.layer_name, metadata = EXCLUDED.metadata;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('roster_cog', 5000, 0), ('roster_cog_protected', 5001, 0), ('roster_dem', 5100, 0),
       ('roster_dem_protected', 5101, 0), ('roster_zarr', 5200, 0), ('roster_zarr_protected', 5201, 0)
ON CONFLICT (service_name, layer_id) DO NOTHING;

-- DEM: 128 x 128 Float32 cells of 0.000625 deg over -122.46..-122.38, 37.72..37.80.
-- elevation(col, row) = 100 + col + 2 * row metres (row 0 is the northern edge), so a
-- client can predict the exact value at any location.
DELETE FROM honua.raster_data WHERE layer_id IN (5100, 5101);
INSERT INTO honua.raster_data (layer_id, name, description, raster, acquisition_date, created_at)
SELECT layer_id, 'roster-dem', 'Deterministic gradient DEM (100 + col + 2*row metres)',
       ST_MapAlgebra(
           ST_AddBand(ST_MakeEmptyRaster(128, 128, -122.46, 37.80, 0.000625, -0.000625, 0, 0, 4326), '32BF'::text, 0, -9999),
           1, '32BF', '100 + [rast.x] - 1 + 2 * ([rast.y] - 1)'),
       '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'
FROM (VALUES (5100), (5101)) AS d(layer_id);

INSERT INTO honua.raster_statistics (raster_data_id, band_number, min_value, max_value, mean_value, std_dev,
                                     valid_pixel_count, nodata_pixel_count)
SELECT id, 1, 100, 100 + 127 + 254, 100 + 63.5 + 127, 0, 128 * 128, 0
FROM honua.raster_data WHERE layer_id IN (5100, 5101);

CREATE OR REPLACE FUNCTION pg_temp.roster_status() RETURNS jsonb LANGUAGE sql AS $$
    SELECT jsonb_build_object('state', 'ready', 'lifecycle', 'active', 'conditions', NULL, 'observedAt', NULL)
$$;

CREATE OR REPLACE FUNCTION pg_temp.roster_metadata(id text, name text, title text) RETURNS jsonb LANGUAGE sql AS $$
    SELECT jsonb_build_object('id', id, 'name', name, 'title', title, 'tags', '[]'::jsonb, 'links', '[]'::jsonb,
        'labels', '{}'::jsonb, 'tenant', NULL, 'themes', '[]'::jsonb, 'license', NULL, 'keywords', '[]'::jsonb,
        'language', NULL, 'createdAt', NULL, 'namespace', NULL, 'publisher', NULL, 'updatedAt', NULL,
        'generation', NULL, 'annotations', '{}'::jsonb, 'attribution', NULL,
        'description', 'Bounded-roster raster fixture', 'contactPoint', NULL)
$$;

DO $roster$
DECLARE
    target record;
    layer record;
    snapshot jsonb;
    access jsonb;
BEGIN
    FOR target IN
        SELECT s.environment, s.revision, s.document
        FROM honua.metadata_v2_snapshots s
        JOIN honua.metadata_v2_current c USING (environment, revision)
    LOOP
        snapshot := target.document;
        -- Idempotent: drop any roster entries from an earlier application first.
        snapshot := jsonb_set(snapshot, '{services}', jsonb_path_query_array(snapshot -> 'services', '$[*] ? (!(@.metadata.id like_regex "^svc-roster-"))'));
        snapshot := jsonb_set(snapshot, '{resources}', jsonb_path_query_array(snapshot -> 'resources', '$[*] ? (!(@.metadata.id like_regex "^res-roster-"))'));
        snapshot := jsonb_set(snapshot, '{storageBindings}', jsonb_path_query_array(snapshot -> 'storageBindings', '$[*] ? (!(@.metadata.id like_regex "^storage-roster-"))'));
        snapshot := jsonb_set(snapshot, '{publications}', jsonb_path_query_array(snapshot -> 'publications', '$[*] ? (!(@.metadata.id like_regex "^pub-roster-"))'));
        FOR layer IN
            SELECT * FROM (VALUES
                (5000, 'roster_cog', 'roster-cog', 'Roster COG', true, ARRAY['ImageServer']),
                (5001, 'roster_cog_protected', 'roster-cog-protected', 'Roster COG (protected)', false, ARRAY['ImageServer']),
                (5100, 'roster_dem', 'roster-dem', 'Roster DEM', true, ARRAY['ImageServer', 'Terrain', 'Elevation']),
                (5101, 'roster_dem_protected', 'roster-dem-protected', 'Roster DEM (protected)', false, ARRAY['ImageServer', 'Terrain', 'Elevation']),
                (5200, 'roster_zarr', 'roster-zarr', 'Roster Zarr', true, ARRAY['ImageServer', 'Wcs', 'OGC-API-Coverages']),
                (5201, 'roster_zarr_protected', 'roster-zarr-protected', 'Roster Zarr (protected)', false, ARRAY['ImageServer', 'Wcs', 'OGC-API-Coverages'])
            ) AS v(layer_index, service_name, slug, title, anonymous, protocols)
        LOOP
            access := jsonb_build_object('allowedRoles', NULL, 'allowAnonymous', layer.anonymous,
                                         'allowedWriteRoles', NULL, 'allowAnonymousWrite', false);
            snapshot := jsonb_set(snapshot, '{services}', (snapshot -> 'services') || jsonb_build_array(jsonb_build_object(
                'route', NULL, 'status', pg_temp.roster_status(), 'options', '{}'::jsonb,
                'metadata', pg_temp.roster_metadata('svc-' || layer.slug || '-image', layer.service_name, layer.service_name),
                'settings', NULL, 'protocols', to_jsonb(layer.protocols), 'extensions', '{}'::jsonb,
                'serviceType', 'esri-image-service', 'accessPolicy', access,
                'publicationIds', jsonb_build_array('pub-' || layer.slug || '-image-' || layer.layer_index),
                'enabledProtocols', to_jsonb(layer.protocols), 'spatialReference', NULL)));
            snapshot := jsonb_set(snapshot, '{resources}', (snapshot -> 'resources') || jsonb_build_array(jsonb_build_object(
                'type', 'raster-dataset', 'style', NULL, 'status', pg_temp.roster_status(), 'display', NULL, 'editing', NULL,
                'spatial', jsonb_build_object(
                    'bbox', jsonb_build_object('east', -122.38, 'west', -122.46, 'north', 37.80, 'south', 37.72),
                    'storageCrs', NULL, 'geometryType', 'point', 'supportedCrs', NULL,
                    'spatialReference', jsonb_build_object('crs', 'EPSG:4326', 'srid', 4326, 'isGeographic', true),
                    'primaryGeometryField', NULL, 'storageCrsCoordinateEpoch', NULL),
                'metadata', pg_temp.roster_metadata('res-' || layer.slug || '-' || layer.layer_index, layer.slug, layer.title),
                'subtypes', NULL, 'temporal', NULL, 'extrusion', NULL, 'policyIds', '[]'::jsonb, 'extensions', '{}'::jsonb,
                'symbology3D', NULL, 'accessPolicy', access, 'schemaFields', '[]'::jsonb, 'relationships', '[]'::jsonb,
                'attributeRules', '[]'::jsonb, 'ownerEditPolicy', NULL, 'permanentFilter', NULL,
                'styleResourceIds', '[]'::jsonb,
                'storageBindingIds', jsonb_build_array('storage-' || layer.slug || '-' || layer.layer_index),
                'contingentValueGroups', '[]'::jsonb,
                'primaryStorageBindingId', 'storage-' || layer.slug || '-' || layer.layer_index)));
            snapshot := jsonb_set(snapshot, '{storageBindings}', (snapshot -> 'storageBindings') || jsonb_build_array(jsonb_build_object(
                'status', pg_temp.roster_status(), 'locator', 'honua.raster_data', 'options', '{}'::jsonb,
                'metadata', pg_temp.roster_metadata('storage-' || layer.slug || '-' || layer.layer_index,
                                                    'storage-' || layer.slug || '-' || layer.layer_index, NULL),
                'extensions', '{}'::jsonb, 'resourceId', 'res-' || layer.slug || '-' || layer.layer_index,
                'storageType', 'relational-table', 'capabilities', '["query", "filter", "render", "tile", "download"]'::jsonb,
                'connectionId', NULL, 'storageLayerId', layer.layer_index)));
            snapshot := jsonb_set(snapshot, '{publications}', (snapshot -> 'publications') || jsonb_build_array(jsonb_build_object(
                'path', layer.layer_index::text, 'status', pg_temp.roster_status(), 'options', '{}'::jsonb,
                'metadata', pg_temp.roster_metadata('pub-' || layer.slug || '-image-' || layer.layer_index,
                                                    layer.layer_index::text, layer.title),
                'isPrimary', true, 'serviceId', 'svc-' || layer.slug || '-image', 'extensions', '{}'::jsonb,
                'identifier', NULL, 'layerIndex', layer.layer_index,
                'resourceId', 'res-' || layer.slug || '-' || layer.layer_index, 'capabilities', '[]'::jsonb,
                'fieldAliases', NULL, 'titleOverride', NULL, 'serviceLocalId', layer.layer_index::text,
                'publicationType', 'esri-image-layer',
                'storageBindingId', 'storage-' || layer.slug || '-' || layer.layer_index, 'supportedFormats', '[]'::jsonb)));
        END LOOP;

        UPDATE honua.metadata_v2_snapshots
           SET document = snapshot, etag = '"' || md5(snapshot::text) || '"'
         WHERE environment = target.environment AND revision = target.revision;
        UPDATE honua.metadata_v2_current
           SET etag = '"' || md5(snapshot::text) || '"', activated_at = NOW()
         WHERE environment = target.environment AND revision = target.revision;
    END LOOP;
END
$roster$;

COMMIT;
