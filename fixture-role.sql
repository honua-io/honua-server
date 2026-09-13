-- #4577 client_credentials probe: a resource restricted to a non-admin role
-- name. A managed key whose permission label equals that role name is a
-- "scoped-api-key" principal on the direct API-key transport; an exchange must
-- not turn the label into the role.
INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
VALUES ('arcgis_compat_role', 'role-restricted', 4326, ARRAY['JSON','GeoJSON'], ARRAY['Query'],
        ST_MakeEnvelope(-122.45, 37.74, -122.38, 37.80, 4326),
        jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('field-editor'))))
ON CONFLICT (service_name) DO UPDATE SET metadata = EXCLUDED.metadata, updated_at = NOW();

INSERT INTO honua.layers (layer_id, layer_name, description, table_schema, table_name, primary_key_column,
    geometry_column, geometry_type, srid, storage_srid, storage_options, extent, default_visibility, enabled, metadata)
VALUES (2030, 'Role Points', 'role-restricted', 'public', 'features', 'objectid', 'geometry', 'Point', 4326, 4326,
        jsonb_build_object('attributesColumn', 'attributes'), ST_MakeEnvelope(-122.44, 37.76, -122.40, 37.79, 4326), true, true,
        jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false, 'allowedRoles', jsonb_build_array('field-editor'))))
ON CONFLICT (layer_id) DO UPDATE SET metadata = EXCLUDED.metadata;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('arcgis_compat_role', 2030, 0)
ON CONFLICT (service_name, layer_id) DO NOTHING;

INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
VALUES (2030, 'objectid', 'Integer', 0, false, 'Object ID'), (2030, 'name', 'String', 1, true, 'Name')
ON CONFLICT DO NOTHING;

INSERT INTO public.features (layer_id, geometry, attributes)
SELECT 2030, ST_SetSRID(ST_MakePoint(-122.42, 37.775), 4326), jsonb_build_object('name', 'role-alpha')
WHERE NOT EXISTS (SELECT 1 FROM public.features WHERE layer_id = 2030);

SELECT honua.seed_metadata_v2_compat_snapshot();
