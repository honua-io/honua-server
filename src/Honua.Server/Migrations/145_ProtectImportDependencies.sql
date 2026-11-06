-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:compatibility-review reason=Refuse import replacement when external database objects depend on the target instead of silently deleting them.
CREATE OR REPLACE FUNCTION honua.swap_import_table(
    schema_name text,
    table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    staging_name := honua.import_staging_table_name(table_name);

    PERFORM honua.assert_import_table_owned(schema_name, table_name);
    IF NOT honua.assert_import_table_owned(schema_name, staging_name) THEN
        RAISE EXCEPTION 'IMPORT_TARGET_NOT_OWNED: Required staging table %.% is absent', schema_name, staging_name
            USING ERRCODE = 'HN409';
    END IF;
    IF honua.assert_import_table_owned(schema_name, table_name) THEN
        EXECUTE format('DROP TABLE %I.%I RESTRICT', schema_name, table_name);
    END IF;
    EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);

    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_geometry', honua.import_index_name(table_name, 'geometry'));
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_properties', honua.import_index_name(table_name, 'properties'));
END;
$$;
