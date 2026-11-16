-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- History deliberately has no snapshot FK: pruning retained documents must not erase edits.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.metadata_v2_field_history (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    environment TEXT NOT NULL,
    revision BIGINT NOT NULL,
    item_kind TEXT NOT NULL,
    item_id TEXT NOT NULL,
    field_path TEXT[] NOT NULL CHECK (cardinality(field_path) > 0),
    old_value JSONB,
    new_value JSONB,
    actor TEXT NOT NULL,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT transaction_timestamp(),
    CHECK (old_value IS DISTINCT FROM new_value)
);
CREATE INDEX IF NOT EXISTS idx_metadata_v2_field_history_item
    ON $HonuaSchema$.metadata_v2_field_history (environment, item_kind, item_id, id DESC);

-- Arrays are one field; object keys retain their own paths, including extension keys.
-- SQL NULL represents an absent field, distinct from an explicit JSON null.
CREATE OR REPLACE FUNCTION $HonuaSchema$.metadata_v2_field_diff(before_value JSONB, after_value JSONB)
RETURNS TABLE(field_path TEXT[], old_value JSONB, new_value JSONB)
LANGUAGE sql IMMUTABLE AS $$
    WITH RECURSIVE fields(path, previous, current) AS (
        SELECT ARRAY[]::TEXT[], before_value, after_value
        UNION ALL
        SELECT fields.path || keys.key, fields.previous -> keys.key, fields.current -> keys.key
        FROM fields
        CROSS JOIN LATERAL (
            SELECT jsonb_object_keys(CASE WHEN jsonb_typeof(fields.previous) = 'object'
                THEN fields.previous ELSE '{}'::JSONB END) AS key
            UNION
            SELECT jsonb_object_keys(CASE WHEN jsonb_typeof(fields.current) = 'object'
                THEN fields.current ELSE '{}'::JSONB END)
        ) keys
        WHERE (fields.previous IS NULL OR jsonb_typeof(fields.previous) = 'object')
          AND (fields.current IS NULL OR jsonb_typeof(fields.current) = 'object')
    )
    SELECT path, previous, current FROM fields
    WHERE cardinality(path) > 0 AND previous IS DISTINCT FROM current
      AND NOT ((previous IS NULL OR jsonb_typeof(previous) = 'object')
           AND (current IS NULL OR jsonb_typeof(current) = 'object')
           AND (COALESCE(previous, '{}'::JSONB) <> '{}'::JSONB
             OR COALESCE(current, '{}'::JSONB) <> '{}'::JSONB));
$$;

-- Only descriptive metadata is copied, never connection options or other runtime configuration.
CREATE OR REPLACE FUNCTION $HonuaSchema$.metadata_v2_history_items(document JSONB, environment_name TEXT)
RETURNS TABLE(item_kind TEXT, item_id TEXT, metadata JSONB)
LANGUAGE sql IMMUTABLE AS $$
    SELECT 'graph'::TEXT, environment_name, document -> 'metadata'
    UNION ALL
    SELECT collection.key, item.value #>> '{metadata,id}', item.value -> 'metadata'
    FROM jsonb_each(document) collection
    CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(collection.value) = 'array'
        THEN collection.value ELSE '[]'::JSONB END) item
    WHERE jsonb_typeof(item.value -> 'metadata') = 'object'
      AND NULLIF(item.value #>> '{metadata,id}', '') IS NOT NULL;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.metadata_v2_record_field_history()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    previous_document JSONB;
    current_document JSONB;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        SELECT document INTO previous_document FROM $HonuaSchema$.metadata_v2_snapshots
        WHERE environment = OLD.environment AND revision = OLD.revision;
    END IF;
    SELECT document INTO STRICT current_document FROM $HonuaSchema$.metadata_v2_snapshots
    WHERE environment = NEW.environment AND revision = NEW.revision;

    INSERT INTO $HonuaSchema$.metadata_v2_field_history
        (environment, revision, item_kind, item_id, field_path, old_value, new_value, actor)
    SELECT NEW.environment, NEW.revision, COALESCE(previous.item_kind, current.item_kind),
        COALESCE(previous.item_id, current.item_id), diff.field_path, diff.old_value, diff.new_value,
        COALESCE(NULLIF(current_setting('honua.metadata_actor', true), ''), 'system:metadata-publisher')
    FROM $HonuaSchema$.metadata_v2_history_items(previous_document, NEW.environment) previous
    FULL JOIN $HonuaSchema$.metadata_v2_history_items(current_document, NEW.environment) current
        ON previous.item_kind = current.item_kind AND previous.item_id = current.item_id
    CROSS JOIN LATERAL $HonuaSchema$.metadata_v2_field_diff(previous.metadata, current.metadata) diff;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER metadata_v2_current_field_history
AFTER INSERT OR UPDATE ON $HonuaSchema$.metadata_v2_current
FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.metadata_v2_record_field_history();
