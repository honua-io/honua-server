-- Add canonical vocabulary without changing the rc.2 physical storage contract.
-- Direct projections remain automatically writable in both reader versions.
CREATE OR REPLACE VIEW $HonuaSchema$.metadata_v2_objects_idx AS
SELECT environment, revision, resource_id AS object_id, name, namespace,
       type AS object_type, primary_storage_binding_id
FROM $HonuaSchema$.metadata_v2_resources_idx;

CREATE OR REPLACE VIEW $HonuaSchema$.metadata_v2_layers_idx AS
SELECT environment, revision, publication_id AS layer_id, service_id,
       resource_id AS object_id, storage_binding_id, publication_type AS layer_type,
       path, layer_index, service_local_id
FROM $HonuaSchema$.metadata_v2_publications_idx;

CREATE OR REPLACE VIEW $HonuaSchema$.metadata_v2_object_storage_bindings_idx AS
SELECT environment, revision, storage_binding_id, resource_id AS object_id,
       connection_id, storage_type, locator
FROM $HonuaSchema$.metadata_v2_storage_bindings_idx;
