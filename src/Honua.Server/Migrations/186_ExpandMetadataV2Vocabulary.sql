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

-- Keep the rc.2 history identity stable and count each alias pair only once.
-- Legacy key presence wins, including null/empty values retained by older writers.
CREATE OR REPLACE FUNCTION $HonuaSchema$.metadata_v2_history_items(document JSONB, environment_name TEXT)
RETURNS TABLE(item_kind TEXT, item_id TEXT, metadata JSONB)
LANGUAGE sql IMMUTABLE AS $$
    SELECT 'graph'::TEXT, environment_name, document -> 'metadata'
    UNION ALL
    SELECT CASE collection.key WHEN 'objects' THEN 'resources'
               WHEN 'layers' THEN 'publications' ELSE collection.key END,
           item.value #>> '{metadata,id}', item.value -> 'metadata'
    FROM jsonb_each(document) collection
    CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(collection.value) = 'array'
        THEN collection.value ELSE '[]'::JSONB END) item
    WHERE (collection.key <> 'objects' OR NOT document ? 'resources')
      AND (collection.key <> 'layers' OR NOT document ? 'publications')
      AND jsonb_typeof(item.value -> 'metadata') = 'object'
      AND NULLIF(item.value #>> '{metadata,id}', '') IS NOT NULL;
$$;
