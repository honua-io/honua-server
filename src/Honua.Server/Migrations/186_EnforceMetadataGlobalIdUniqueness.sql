-- Unique UUID expressions enforce identity even for concurrent writers and rows hidden by RLS.
-- Activation fails atomically if existing values conflict; no feature is rewritten or removed.
-- Identity indexes persist when metadata changes, including while an older reader serves requests.
CREATE OR REPLACE FUNCTION $HonuaSchema$.sync_metadata_global_id_indexes()
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    identity record;
    index_name text;
    identifier_whitespace constant text := U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680'
        || U&'\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000';
BEGIN
    -- Serialize activation across environments before reading the committed active graphs.
    PERFORM pg_advisory_xact_lock(hashtextextended('$HonuaSchema$:global-ids', 0));

    FOR identity IN
        WITH active_graphs AS (
            SELECT snapshot.document
            FROM $HonuaSchema$.metadata_v2_current current
            JOIN $HonuaSchema$.metadata_v2_snapshots snapshot
                ON snapshot.environment = current.environment AND snapshot.revision = current.revision
        ), normalized_bindings AS (
            SELECT document, binding,
                COALESCE((SELECT jsonb_object_agg(lower(option.key), option.value)
                    FROM jsonb_each(COALESCE(binding->'options', '{}'::jsonb)) option), '{}'::jsonb) AS options,
                CASE WHEN binding->>'locator' !~ '[:/]'
                    THEN ARRAY(SELECT btrim(component, identifier_whitespace)
                        FROM unnest(string_to_array(binding->>'locator', '.')) WITH ORDINALITY AS parts(component, position)
                        WHERE btrim(component, identifier_whitespace) <> '' ORDER BY position) END AS locator_parts
            FROM active_graphs
            CROSS JOIN LATERAL jsonb_array_elements(document->'storageBindings') binding
            WHERE binding->>'storageType' = 'relational-table'
                AND binding->>'storageLayerId' IS NOT NULL
                AND binding->>'connectionId' IS NULL
        ), explicit_names AS (
            SELECT *,
                CASE WHEN binding->'options' ? 'tableName'
                    THEN COALESCE(binding->'options'->>'tableName', '')
                    ELSE binding->'options'->>'table' END AS explicit_table,
                CASE WHEN binding->'options' ? 'schemaName'
                    THEN COALESCE(binding->'options'->>'schemaName', '')
                    ELSE binding->'options'->>'schema' END AS explicit_schema
            FROM normalized_bindings
        ), binding_names AS (
            SELECT *,
                CASE WHEN btrim(COALESCE(explicit_table, ''), identifier_whitespace) = ''
                    THEN trim(both '"' FROM btrim(locator_parts[array_length(locator_parts, 1)], identifier_whitespace))
                    ELSE btrim(explicit_table, identifier_whitespace) END AS table_name,
                btrim(COALESCE(explicit_schema,
                    CASE WHEN btrim(COALESCE(explicit_table, ''), identifier_whitespace) = ''
                        AND array_length(locator_parts, 1) > 1
                        THEN trim(both '"' FROM btrim(locator_parts[array_length(locator_parts, 1) - 1], identifier_whitespace)) END),
                    identifier_whitespace) AS schema_name,
                lower(btrim(COALESCE(options->>'sourcebacked', 'false'), identifier_whitespace)) = 'true' AS source_backed
            FROM explicit_names
        ), resolved_bindings AS (
            SELECT *, to_regclass(CASE WHEN COALESCE(schema_name, '') = '' THEN format('%I', table_name)
                ELSE format('%I.%I', schema_name, table_name) END) AS feature_table
            FROM binding_names WHERE table_name = 'features'
        ), managed_bindings AS (
            SELECT * FROM resolved_bindings binding
            WHERE feature_table IS NOT NULL AND (
                NOT source_backed OR (
                    lower(binding->'options'->>'attributesColumn') = 'attributes'
                    AND lower(binding->'options'->>'layerDiscriminatorColumn') = 'layer_id'
                    AND lower(COALESCE(binding->'options'->>'primaryKeyColumn', 'objectid')) = 'objectid'
                    AND lower(COALESCE(binding->'options'->>'geometryColumn', 'geometry')) = 'geometry'
                    AND (feature_table = to_regclass('features') OR EXISTS (
                        SELECT 1 FROM resolved_bindings native
                        WHERE NOT native.source_backed AND native.feature_table = binding.feature_table
                    ))
                )
            )
        )
        SELECT DISTINCT feature_table, (binding->>'storageLayerId')::integer AS layer_id,
            field->>'name' AS field_name
        FROM managed_bindings
        CROSS JOIN LATERAL jsonb_array_elements(document->'resources') resource
        CROSS JOIN LATERAL jsonb_array_elements(resource->'schemaFields') field
        WHERE lower(field->>'name') = lower(resource->'editing'->>'globalIdField')
            AND binding->>'resourceId' = resource->'metadata'->>'id'
            AND resource->'storageBindingIds' ? (binding->'metadata'->>'id')
    LOOP
        index_name := 'idx_features_gid_' || md5(identity.layer_id::text || ':' || identity.field_name);
        EXECUTE format(
            'CREATE UNIQUE INDEX IF NOT EXISTS %I ON %s ((NULLIF(attributes->>%L, '''')::uuid)) WHERE layer_id = %s',
            index_name, identity.feature_table, identity.field_name, identity.layer_id);
    END LOOP;

END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.enforce_metadata_global_ids()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM $HonuaSchema$.sync_metadata_global_id_indexes();
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER metadata_v2_current_global_ids
AFTER INSERT OR UPDATE OR DELETE ON $HonuaSchema$.metadata_v2_current
FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.enforce_metadata_global_ids();

SELECT $HonuaSchema$.sync_metadata_global_id_indexes();
