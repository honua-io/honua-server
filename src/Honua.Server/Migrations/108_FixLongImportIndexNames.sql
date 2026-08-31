-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Keep the two canonical indexes distinct when replacing a legacy import table
-- whose physical name is too long for idx_<table>_<kind> to fit in 63 bytes.
CREATE OR REPLACE FUNCTION honua.import_index_name(table_name text, index_kind text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    index_name text;
BEGIN
    index_name := 'idx_' || table_name || '_' || index_kind;
    IF length(index_name) > 63 THEN
        index_name := left(index_name, 46) || '_' || left(md5(index_name), 16);
    END IF;

    RETURN index_name;
END;
$$;

CREATE OR REPLACE FUNCTION honua.swap_import_table(schema_name text, table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');
    staging_name := honua.import_staging_table_name(table_name);

    EXECUTE format('DROP TABLE IF EXISTS %I.%I CASCADE', schema_name, table_name);
    EXECUTE format('ALTER TABLE %I.%I RENAME TO %I', schema_name, staging_name, table_name);
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_geometry', honua.import_index_name(table_name, 'geometry'));
    EXECUTE format('ALTER INDEX IF EXISTS %I.%I RENAME TO %I',
        schema_name, 'idx_' || staging_name || '_properties', honua.import_index_name(table_name, 'properties'));
END;
$$;
