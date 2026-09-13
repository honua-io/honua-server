-- #4577 candidate replay fixture, mirroring honua-client-compat
-- docker/seed/arcgis-compat.sql (the SERVER-015 diagnostic): the protected
-- service admits only role "admin"; the scoped service admits the server-owned
-- "scoped-api-key" role. Applied after the candidate migrated; compiled into
-- Metadata v2 by the candidate revision's own seed_metadata_v2_compat_snapshot()
-- (from tests/seed/client-compat-v1.sql).
INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
VALUES
    ('arcgis_compat_protected', 'protected', 4326, ARRAY['JSON','GeoJSON'], ARRAY['Query'],
     ST_MakeEnvelope(-122.45, 37.74, -122.38, 37.80, 4326),
     jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('admin')))),
    ('arcgis_compat_scoped', 'scoped', 4326, ARRAY['JSON','GeoJSON'], ARRAY['Query'],
     ST_MakeEnvelope(-122.45, 37.74, -122.38, 37.80, 4326),
     jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('scoped-api-key'))))
ON CONFLICT (service_name) DO UPDATE SET metadata = EXCLUDED.metadata, updated_at = NOW();

INSERT INTO honua.layers (layer_id, layer_name, description, table_schema, table_name, primary_key_column,
    geometry_column, geometry_type, srid, storage_srid, storage_options, extent, default_visibility, enabled, metadata)
VALUES
    (2010, 'Protected Points', 'protected', 'public', 'features', 'objectid', 'geometry', 'Point', 4326, 4326,
     jsonb_build_object('attributesColumn', 'attributes'), ST_MakeEnvelope(-122.44, 37.76, -122.40, 37.79, 4326), true, true,
     jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('admin')))),
    (2020, 'Scoped Points', 'scoped', 'public', 'features', 'objectid', 'geometry', 'Point', 4326, 4326,
     jsonb_build_object('attributesColumn', 'attributes'), ST_MakeEnvelope(-122.44, 37.76, -122.40, 37.79, 4326), true, true,
     jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('scoped-api-key'))))
ON CONFLICT (layer_id) DO UPDATE SET metadata = EXCLUDED.metadata;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('arcgis_compat_protected', 2010, 0), ('arcgis_compat_scoped', 2020, 0)
ON CONFLICT (service_name, layer_id) DO NOTHING;

INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
VALUES
    (2010, 'objectid', 'Integer', 0, false, 'Object ID'), (2010, 'name', 'String', 1, true, 'Name'),
    (2020, 'objectid', 'Integer', 0, false, 'Object ID'), (2020, 'name', 'String', 1, true, 'Name')
ON CONFLICT DO NOTHING;

INSERT INTO public.features (layer_id, geometry, attributes)
SELECT l, ST_SetSRID(ST_MakePoint(-122.42, 37.775), 4326), jsonb_build_object('name', n)
FROM (VALUES (2010, 'protected-alpha'), (2020, 'scoped-alpha')) AS f(l, n)
WHERE NOT EXISTS (SELECT 1 FROM public.features WHERE layer_id = f.l);

SELECT honua.seed_metadata_v2_compat_snapshot();
