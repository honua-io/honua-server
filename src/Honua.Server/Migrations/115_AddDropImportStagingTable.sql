-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- honua:compatibility-review reason=Adds a new helper that only drops the never-promoted <table>__staging sibling; no existing table, function signature, or import contract changes.

-- A Replace import whose load dropped rows against an already-populated target must not
-- promote its incomplete staging sibling over the live table (#4006). The staging table is
-- then never promoted and has to be cleaned up. The file-import path keeps every DDL
-- statement in these numbered migrations -- application code only calls the honua.* helpers --
-- so the cleanup drop belongs here alongside honua.create_import_staging_table and
-- honua.swap_import_table rather than in an inline command string.
CREATE OR REPLACE FUNCTION honua.drop_import_staging_table(schema_name text, table_name text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    staging_name text;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');
    staging_name := honua.import_staging_table_name(table_name);

    EXECUTE format('DROP TABLE IF EXISTS %I.%I', schema_name, staging_name);
END;
$$;
