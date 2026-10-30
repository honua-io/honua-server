-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Promote source attributes out of an import-owned table's properties jsonb into
-- nullable typed columns. The jsonb document stays complete. Keys that are unsafe,
-- reserved, nested, or not coercible are left only in jsonb. A table whose comment
-- is not the importer ownership marker is left unchanged, so customer tables and
-- already-imported tables that are not re-imported keep their current shape.
-- No session state and no advisory lock: the caller already holds the import lock.

CREATE OR REPLACE FUNCTION honua.import_typed_bigint(props jsonb, key text)
RETURNS bigint
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN props IS NULL OR key IS NULL THEN NULL
        WHEN jsonb_typeof(props -> key) IN ('number', 'string')
         AND (props ->> key) ~ '^-?[0-9]+$'
         AND length(ltrim(props ->> key, '-')) BETWEEN 1 AND 19
         AND (props ->> key)::numeric BETWEEN -9223372036854775808 AND 9223372036854775807
        THEN (props ->> key)::bigint
        ELSE NULL
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
    IF raw IS NULL OR length(raw) = 0 OR length(raw) > 64 THEN
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
    caster_call text;
    assignments text := '';
    sep text := '';
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

    FOR col IN SELECT value FROM jsonb_array_elements(columns)
    LOOP
        IF COALESCE(cardinality(applied_names), 0) >= 128 THEN
            EXIT;
        END IF;

        existing_type := NULL;
        col_name := col ->> 'name';
        requested_type := col ->> 'pgType';

        IF col_name IS NULL
           OR col_name !~ '^[A-Za-z_][A-Za-z0-9_]*$'
           OR length(col_name) > 63
           OR lower(col_name) IN ('id', 'geometry', 'properties', 'created_at') THEN
            CONTINUE;
        END IF;

        IF col_name = ANY(applied_names) THEN
            CONTINUE;
        END IF;

        IF requested_type NOT IN (
            'bigint',
            'double precision',
            'boolean',
            'text',
            'date',
            'timestamp with time zone') THEN
            CONTINUE;
        END IF;

        SELECT c.data_type
          INTO existing_type
          FROM information_schema.columns AS c
         WHERE c.table_schema = promote_import_typed_columns.schema_name
           AND c.table_name = promote_import_typed_columns.table_name
           AND c.column_name = col_name;

        IF existing_type IS NULL THEN
            EXECUTE format(
                'ALTER TABLE %I.%I ADD COLUMN %I %s',
                schema_name, table_name, col_name, requested_type);
            existing_type := requested_type;
        ELSIF existing_type NOT IN (
            'bigint',
            'double precision',
            'boolean',
            'text',
            'date',
            'timestamp with time zone') THEN
            CONTINUE;
        END IF;

        caster_call := CASE existing_type
            WHEN 'bigint' THEN format('honua.import_typed_bigint(properties, %L)', col_name)
            WHEN 'double precision' THEN format('honua.import_typed_double(properties, %L)', col_name)
            WHEN 'boolean' THEN format('honua.import_typed_boolean(properties, %L)', col_name)
            WHEN 'text' THEN format('honua.import_typed_text(properties, %L)', col_name)
            WHEN 'date' THEN format('honua.import_typed_date(properties, %L)', col_name)
            WHEN 'timestamp with time zone' THEN format('honua.import_typed_timestamptz(properties, %L)', col_name)
            ELSE NULL
        END;

        IF caster_call IS NULL THEN
            CONTINUE;
        END IF;

        assignments := assignments || sep || format('%I = %s', col_name, caster_call);
        sep := ', ';
        applied_names := applied_names || col_name;
        applied_types := applied_types || existing_type;
    END LOOP;

    IF assignments <> '' THEN
        EXECUTE format('UPDATE %I.%I SET %s', schema_name, table_name, assignments);
    END IF;

    FOR applied_index IN 1 .. COALESCE(cardinality(applied_names), 0)
    LOOP
        column_name := applied_names[applied_index];
        postgres_type := applied_types[applied_index];
        RETURN NEXT;
    END LOOP;
END;
$$;
