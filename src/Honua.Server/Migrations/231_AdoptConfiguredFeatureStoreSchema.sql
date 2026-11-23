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
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    -- Only the Honua shared table qualifies: a user table that is merely named features
    -- does not carry the layer_id discriminator and the JSONB attributes column.
    SELECT coalesce(array_agg(candidate.schema_name ORDER BY candidate.schema_name), '{}')
    INTO shared_sources
    FROM unnest(ARRAY['honua', 'public']) AS candidate(schema_name)
    WHERE candidate.schema_name <> target_schema
      AND (SELECT count(*)
           FROM pg_catalog.pg_attribute AS attribute
           WHERE attribute.attrelid = to_regclass(format('%I.%I', candidate.schema_name, 'features'))
             AND NOT attribute.attisdropped
             AND ((attribute.attname = 'layer_id' AND attribute.atttypid = 'integer'::regtype)
               OR (attribute.attname = 'attributes' AND attribute.atttypid = 'jsonb'::regtype))) = 2;

    IF to_regclass(format('%I.%I', target_schema, 'features')) IS NOT NULL THEN
        IF cardinality(shared_sources) > 0 THEN
            RAISE EXCEPTION
                'Cannot adopt feature store schema: %.features exists and a shared features table also exists in %',
                target_schema, array_to_string(shared_sources, ', ');
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
