-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- 001 creates the shared managed features table unqualified and migrations run with
-- search_path public, so under a non-default Database:Schema it sits in public (or honua on
-- older hand-built databases). The publish check, the managed writer and the attachment store
-- address the configured schema, so move the table there without copying rows. Indexes,
-- constraints, the owned objectid sequence and any trigger attached to it move with it.
-- Later migrations must name this table as $HonuaSchema$.features, never bare features.
-- honua:migration-phase contract
-- honua:compatibility-review reason=#9627 nodes on a non-default schema already write $HonuaSchema$.features; old nodes must be drained before the table moves

CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

DO $$
DECLARE
    target_schema text := (SELECT nspname FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    shared_sources text[];
    target_relation regclass := to_regclass(format('%I.%I', target_schema, 'features'));
    target_is_managed boolean;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    -- Count only managed stores. An unrelated source view or table must not prevent
    -- adoption, but any relation occupying the destination must be left in place.
    WITH managed_stores AS (
        SELECT n.nspname::text AS nspname
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
        LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
        WHERE n.nspname IN ('honua', 'public', target_schema)
          AND c.relname = 'features' AND c.relkind = 'r'
          AND a.attnum > 0 AND NOT a.attisdropped
          AND CASE a.attname
            WHEN 'objectid' THEN a.atttypid = 'bigint'::regtype AND a.attnotnull
                AND pg_catalog.pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), 'objectid') IS NOT NULL
                AND (a.attidentity <> '' OR pg_catalog.pg_get_expr(d.adbin, d.adrelid) LIKE 'nextval(%')
                AND EXISTS (
                    SELECT 1 FROM pg_catalog.pg_index i
                    WHERE i.indrelid = c.oid AND i.indisprimary AND i.indisvalid
                      AND i.indnkeyatts = 1 AND i.indkey[0] = a.attnum)
            WHEN 'layer_id' THEN a.atttypid = 'integer'::regtype
            WHEN 'geometry' THEN a.atttypid = (
                SELECT t.oid FROM pg_catalog.pg_type t
                JOIN pg_catalog.pg_extension e ON e.extnamespace = t.typnamespace
                WHERE e.extname = 'postgis' AND t.typname = 'geometry')
            WHEN 'attributes' THEN a.atttypid = 'jsonb'::regtype
            WHEN 'created_at' THEN a.atttypid = 'timestamptz'::regtype
            WHEN 'updated_at' THEN a.atttypid = 'timestamptz'::regtype
            ELSE FALSE
          END
        GROUP BY n.nspname, c.oid
        HAVING count(*) = 6
    )
    SELECT coalesce(array_agg(nspname ORDER BY nspname) FILTER (WHERE nspname <> target_schema), '{}'),
           count(*) FILTER (WHERE nspname = target_schema) = 1
    INTO shared_sources, target_is_managed
    FROM managed_stores;

    IF target_relation IS NOT NULL THEN
        IF cardinality(shared_sources) > 0 THEN
            RAISE EXCEPTION
                'Cannot adopt feature store schema: %.features exists and a shared features table also exists in %',
                target_schema, array_to_string(shared_sources, ', ');
        END IF;
        IF NOT target_is_managed THEN
            RAISE EXCEPTION 'Cannot adopt feature store schema: % is incompatible with the managed feature store',
                target_relation;
        END IF;
        RETURN;
    END IF;

    IF cardinality(shared_sources) <> 1 THEN
        RAISE EXCEPTION
            'Cannot adopt feature store schema: expected one shared features table in honua or public, found % (%)',
            cardinality(shared_sources), array_to_string(shared_sources, ', ');
    END IF;

    EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', shared_sources[1], 'features', target_schema);

    -- Managed-store registry rows that recorded the old location follow the table (227 has
    -- already moved the registry into the configured schema). Source-backed rows name their
    -- own source table, which may be a user table called features on another database.
    IF to_regclass(format('%I.%I', target_schema, 'layers')) IS NOT NULL THEN
        EXECUTE format(
            'UPDATE %I.layers SET table_schema = %L WHERE table_name = %L AND coalesce(table_schema, %L) IN (%L, %L)'
            ' AND storage_options ->> %L = %L',
            target_schema, target_schema, 'features', '', '', shared_sources[1], 'managedStore', 'true');
    END IF;
END
$$;
