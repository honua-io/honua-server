-- Unique UUID expressions enforce identity even for concurrent writers and rows hidden by RLS.
-- Activation fails atomically if existing values conflict; no feature is rewritten or removed.
-- Identity indexes persist when metadata changes, including while an older reader serves requests.
CREATE OR REPLACE FUNCTION $HonuaSchema$.sync_metadata_global_id_indexes()
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    feature_table regclass := to_regclass('$HonuaSchema$.features');
    feature_schema text;
    identity record;
    index_name text;
BEGIN
    IF feature_table IS NULL THEN
        RETURN;
    END IF;

    SELECT namespace.nspname INTO feature_schema
    FROM pg_class relation JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
    WHERE relation.oid = feature_table;

    -- Separate statements obtain a fresh READ COMMITTED snapshot after another environment commits.
    PERFORM pg_advisory_xact_lock(hashtextextended(feature_table::text || ':global-ids', 0));

    FOR identity IN
        WITH active_graphs AS (
            SELECT snapshot.document
            FROM $HonuaSchema$.metadata_v2_current current
            JOIN $HonuaSchema$.metadata_v2_snapshots snapshot
                ON snapshot.environment = current.environment AND snapshot.revision = current.revision
        ), declarations AS (
            SELECT resource, binding, field->>'name' AS field_name
            FROM active_graphs
            CROSS JOIN LATERAL jsonb_array_elements(document->'resources') resource
            CROSS JOIN LATERAL jsonb_array_elements(document->'storageBindings') binding
            CROSS JOIN LATERAL jsonb_array_elements(resource->'schemaFields') field
            WHERE lower(field->>'name') = lower(resource->'editing'->>'globalIdField')
                AND binding->>'resourceId' = resource->'metadata'->>'id'
                AND resource->'storageBindingIds' ? (binding->'metadata'->>'id')
                AND binding->>'storageType' = 'relational-table'
                AND binding->>'storageLayerId' IS NOT NULL
        )
        SELECT DISTINCT (binding->>'storageLayerId')::integer AS layer_id, field_name
        FROM declarations
        WHERE NOT EXISTS (
            SELECT 1 FROM jsonb_each(COALESCE(binding->'options', '{}'::jsonb)) option
            WHERE lower(option.key) = 'sourcebacked' AND lower(btrim(option.value #>> '{}')) = 'true'
        ) OR (
            lower(COALESCE(NULLIF(binding->'options'->>'tableName', ''),
                NULLIF(binding->'options'->>'table', ''),
                trim(both '"' FROM btrim(regexp_replace(binding->>'locator', '^.*\.', ''))))) = 'features'
            AND lower(binding->'options'->>'attributesColumn') = 'attributes'
            AND lower(binding->'options'->>'layerDiscriminatorColumn') = 'layer_id'
        )
    LOOP
        index_name := 'idx_features_gid_' || md5(identity.layer_id::text || ':' || identity.field_name);
        EXECUTE format(
            'CREATE UNIQUE INDEX IF NOT EXISTS %I ON %I.features ((NULLIF(attributes->>%L, '''')::uuid)) WHERE layer_id = %s',
            index_name, feature_schema, identity.field_name, identity.layer_id);
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
