-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Promote source attributes out of an import-owned table's properties jsonb into
-- nullable typed columns. The jsonb document stays complete. Keys that are unsafe,
-- reserved (including PostgreSQL system columns), nested, or not coercible are left
-- only in jsonb. A table whose comment is not the importer ownership marker is left
-- unchanged, so customer tables and already-imported tables that are not re-imported
-- keep their current shape.
-- Adding a column and backfilling it are separate functions. The caller commits the
-- catalog change before the update, so the access-exclusive lock is not held while
-- every row is rewritten. Backfill walks primary-key batches and commits between
-- them. No session state and no advisory lock.

CREATE OR REPLACE FUNCTION honua.import_typed_bigint(props jsonb, key text)
RETURNS bigint
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    raw text;
    value numeric;
BEGIN
    IF props IS NULL OR key IS NULL OR jsonb_typeof(props -> key) NOT IN ('number', 'string') THEN
        RETURN NULL;
    END IF;
    raw := props ->> key;
    IF raw IS NULL OR length(raw) NOT BETWEEN 1 AND 64 OR raw !~ '^-?[0-9]+(\.0+)?$' THEN
        RETURN NULL;
    END IF;
    value := raw::numeric;
    IF value NOT BETWEEN -9223372036854775808 AND 9223372036854775807 THEN
        RETURN NULL;
    END IF;
    RETURN value::bigint;
END;
$$;

CREATE OR REPLACE FUNCTION honua.import_typed_double(props jsonb, key text)
RETURNS double precision
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    raw text;
BEGIN
    IF props IS NULL OR key IS NULL OR jsonb_typeof(props -> key) NOT IN ('number', 'string') THEN
        RETURN NULL;
    END IF;
    raw := props ->> key;
    -- JSONB expands exponent notation to decimal text. Allow the full range of
    -- finite doubles while keeping string coercion bounded.
    IF raw IS NULL OR length(raw) = 0
       OR (jsonb_typeof(props -> key) = 'number' AND length(raw) > 1024)
       OR (jsonb_typeof(props -> key) = 'string' AND length(raw) > 64) THEN
        RETURN NULL;
    END IF;
    IF raw !~ '^-?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?$' THEN
        RETURN NULL;
    END IF;
    RETURN raw::double precision;
EXCEPTION
    WHEN invalid_text_representation OR numeric_value_out_of_range THEN
        RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION honua.import_typed_boolean(props jsonb, key text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN props IS NULL OR key IS NULL THEN NULL
        WHEN jsonb_typeof(props -> key) = 'boolean' THEN (props ->> key)::boolean
        WHEN jsonb_typeof(props -> key) = 'string' AND lower(props ->> key) IN ('true', 'false')
            THEN lower(props ->> key)::boolean
        ELSE NULL
    END;
$$;

CREATE OR REPLACE FUNCTION honua.import_typed_text(props jsonb, key text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN props IS NULL OR key IS NULL THEN NULL
        WHEN jsonb_typeof(props -> key) IN ('string', 'number', 'boolean') THEN props ->> key
        ELSE NULL
    END;
$$;

CREATE OR REPLACE FUNCTION honua.import_typed_date(props jsonb, key text)
RETURNS date
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    raw text;
BEGIN
    IF props IS NULL OR key IS NULL OR jsonb_typeof(props -> key) <> 'string' THEN
        RETURN NULL;
    END IF;
    raw := props ->> key;
    IF raw IS NULL OR raw !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN
        RETURN NULL;
    END IF;
    RETURN raw::date;
EXCEPTION
    WHEN invalid_datetime_format OR datetime_field_overflow THEN
        RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION honua.import_typed_timestamptz(props jsonb, key text)
RETURNS timestamp with time zone
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    raw text;
BEGIN
    IF props IS NULL OR key IS NULL OR jsonb_typeof(props -> key) <> 'string' THEN
        RETURN NULL;
    END IF;
    raw := props ->> key;
    IF raw IS NULL OR raw !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}' THEN
        RETURN NULL;
    END IF;
    RETURN raw::timestamp with time zone;
EXCEPTION
    WHEN invalid_datetime_format OR datetime_field_overflow OR invalid_text_representation THEN
        RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION honua.promote_import_typed_columns(
    schema_name text,
    table_name text,
    columns jsonb)
RETURNS TABLE(column_name text, postgres_type text)
LANGUAGE plpgsql
AS $$
DECLARE
    col jsonb;
    col_name text;
    requested_type text;
    existing_type text;
    existing_record record;
    existing_promoted integer := 0;
    added_count integer := 0;
    applied_names text[] := ARRAY[]::text[];
    applied_types text[] := ARRAY[]::text[];
    applied_index integer;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    IF to_regclass(format('%I.%I', schema_name, table_name)) IS NULL THEN
        RETURN;
    END IF;

    IF obj_description(to_regclass(format('%I.%I', schema_name, table_name)), 'pg_class')
        IS DISTINCT FROM 'honua:import-owned:v1' THEN
        RETURN;
    END IF;

    IF columns IS NULL OR jsonb_typeof(columns) <> 'array' THEN
        RETURN;
    END IF;

    SELECT count(*)
      INTO existing_promoted
      FROM information_schema.columns AS c
     WHERE c.table_schema = promote_import_typed_columns.schema_name
       AND c.table_name = promote_import_typed_columns.table_name
       AND honua.import_typed_column_allowed(c.column_name, c.data_type);

    FOR col IN SELECT value FROM jsonb_array_elements(columns)
    LOOP
        existing_type := NULL;
        col_name := col ->> 'name';
        requested_type := col ->> 'pgType';

        IF NOT honua.import_typed_column_allowed(col_name, requested_type)
           OR col_name = ANY(applied_names) THEN
            CONTINUE;
        END IF;

        SELECT c.data_type
          INTO existing_type
          FROM information_schema.columns AS c
         WHERE c.table_schema = promote_import_typed_columns.schema_name
           AND c.table_name = promote_import_typed_columns.table_name
           AND c.column_name = col_name;

        IF existing_type IS NULL THEN
            -- Layer publication treats field names case-insensitively. Do not add
            -- a second physical column that differs from an existing one only by case.
            IF EXISTS (
                SELECT 1 FROM information_schema.columns AS c
                 WHERE c.table_schema = promote_import_typed_columns.schema_name
                   AND c.table_name = promote_import_typed_columns.table_name
                   AND lower(c.column_name) = lower(col_name)) THEN
                CONTINUE;
            END IF;

            IF existing_promoted + added_count >= 128 THEN
                CONTINUE;
            END IF;

            EXECUTE format(
                'ALTER TABLE %I.%I ADD COLUMN %I %s',
                schema_name, table_name, col_name, requested_type);
            existing_type := requested_type;
            added_count := added_count + 1;
        ELSIF NOT honua.import_typed_column_allowed(col_name, existing_type) THEN
            CONTINUE;
        END IF;

        applied_names := applied_names || col_name;
        applied_types := applied_types || existing_type;
    END LOOP;

    -- A later import may omit a key whose column already exists. Keep that column in
    -- the result so the backfill clears it when the stored document no longer coerces.
    FOR existing_record IN
        SELECT c.column_name, c.data_type
          FROM information_schema.columns AS c
         WHERE c.table_schema = promote_import_typed_columns.schema_name
           AND c.table_name = promote_import_typed_columns.table_name
           AND honua.import_typed_column_allowed(c.column_name, c.data_type)
           AND NOT (c.column_name = ANY(applied_names))
         ORDER BY c.ordinal_position
    LOOP
        applied_names := applied_names || existing_record.column_name;
        applied_types := applied_types || existing_record.data_type;
    END LOOP;

    FOR applied_index IN 1 .. COALESCE(cardinality(applied_names), 0)
    LOOP
        column_name := applied_names[applied_index];
        postgres_type := applied_types[applied_index];
        RETURN NEXT;
    END LOOP;
END;
$$;

-- One primary-key batch of the backfill. Returns the highest id written, or NULL when
-- no row remains. The caller commits between batches.
CREATE OR REPLACE FUNCTION honua.backfill_import_typed_columns(
    schema_name text,
    table_name text,
    columns jsonb,
    after_id bigint,
    batch_size integer)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    col jsonb;
    col_name text;
    requested_type text;
    existing_type text;
    assignment text;
    assignments text := '';
    sep text := '';
    seen text[] := ARRAY[]::text[];
    last_id bigint;
BEGIN
    PERFORM honua.assert_import_identifier(schema_name, 'Schema name');
    PERFORM honua.assert_import_identifier(table_name, 'Table name');

    IF to_regclass(format('%I.%I', schema_name, table_name)) IS NULL THEN
        RETURN NULL;
    END IF;

    IF obj_description(to_regclass(format('%I.%I', schema_name, table_name)), 'pg_class')
        IS DISTINCT FROM 'honua:import-owned:v1' THEN
        RETURN NULL;
    END IF;

    IF columns IS NULL OR jsonb_typeof(columns) <> 'array' THEN
        RETURN NULL;
    END IF;

    IF after_id IS NULL OR after_id < 0 THEN
        after_id := 0;
    END IF;

    IF batch_size IS NULL OR batch_size < 1 THEN
        batch_size := 50000;
    ELSIF batch_size > 100000 THEN
        batch_size := 100000;
    END IF;

    FOR col IN SELECT value FROM jsonb_array_elements(columns)
    LOOP
        IF COALESCE(cardinality(seen), 0) >= 128 THEN
            EXIT;
        END IF;

        existing_type := NULL;
        col_name := col ->> 'name';
        requested_type := col ->> 'pgType';

        IF NOT honua.import_typed_column_allowed(col_name, requested_type)
           OR col_name = ANY(seen) THEN
            CONTINUE;
        END IF;

        SELECT c.data_type
          INTO existing_type
          FROM information_schema.columns AS c
         WHERE c.table_schema = backfill_import_typed_columns.schema_name
           AND c.table_name = backfill_import_typed_columns.table_name
           AND c.column_name = col_name;

        -- Keep a column that already has an allowlisted type. Do not add columns here,
        -- and do not change a column whose type is outside the allowlist.
        IF existing_type IS NULL
           OR NOT honua.import_typed_column_allowed(col_name, existing_type) THEN
            CONTINUE;
        END IF;

        assignment := CASE existing_type
            WHEN 'bigint' THEN format('%I = honua.import_typed_bigint(properties, %L)', col_name, col_name)
            WHEN 'double precision' THEN format('%I = honua.import_typed_double(properties, %L)', col_name, col_name)
            WHEN 'boolean' THEN format('%I = honua.import_typed_boolean(properties, %L)', col_name, col_name)
            WHEN 'text' THEN format('%I = honua.import_typed_text(properties, %L)', col_name, col_name)
            WHEN 'date' THEN format('%I = honua.import_typed_date(properties, %L)', col_name, col_name)
            WHEN 'timestamp with time zone' THEN format('%I = honua.import_typed_timestamptz(properties, %L)', col_name, col_name)
            ELSE NULL
        END;

        IF assignment IS NULL THEN
            CONTINUE;
        END IF;

        assignments := assignments || sep || assignment;
        sep := ', ';
        seen := seen || col_name;
    END LOOP;

    IF assignments = '' THEN
        RETURN NULL;
    END IF;

    EXECUTE format(
        'WITH picked AS (
            SELECT id FROM %I.%I WHERE id > $1 ORDER BY id LIMIT $2
         ), updated AS (
            UPDATE %I.%I AS target
               SET %s
              FROM picked
             WHERE target.id = picked.id
            RETURNING target.id
         )
         SELECT max(id) FROM updated',
        schema_name, table_name,
        schema_name, table_name,
        assignments)
    INTO last_id
    USING after_id, batch_size;

    RETURN last_id;
END;
$$;

-- Names and types that may be projected out of properties. System columns cannot be
-- added, and the import's own id, geometry, properties, and created_at stay put.
CREATE OR REPLACE FUNCTION honua.import_typed_column_allowed(col_name text, postgres_type text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT col_name IS NOT NULL
       AND postgres_type IS NOT NULL
       AND col_name ~ '^[A-Za-z_][A-Za-z0-9_]*$'
       AND length(col_name) <= 63
       AND lower(col_name) NOT IN (
            'id', 'geometry', 'properties', 'created_at',
            'xmin', 'xmax', 'cmin', 'cmax', 'ctid', 'tableoid')
       AND postgres_type IN (
            'bigint',
            'double precision',
            'boolean',
            'text',
            'date',
            'timestamp with time zone');
$$;
