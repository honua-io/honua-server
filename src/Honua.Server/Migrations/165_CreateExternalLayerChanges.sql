CREATE TABLE IF NOT EXISTS $HonuaSchema$.external_layer_revisions (
    layer_id integer PRIMARY KEY,
    enabled boolean NOT NULL DEFAULT false,
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.external_layer_changes (
    layer_id integer NOT NULL REFERENCES $HonuaSchema$.external_layer_revisions(layer_id),
    revision bigint NOT NULL CHECK (revision > 0),
    extent double precision[],
    changed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (layer_id, revision),
    CONSTRAINT external_layer_changes_extent CHECK (
        extent IS NULL OR (
            array_ndims(extent) = 1 AND array_lower(extent, 1) = 1 AND cardinality(extent) = 4
            AND extent[1] IS NOT NULL AND extent[2] IS NOT NULL
            AND extent[3] IS NOT NULL AND extent[4] IS NOT NULL
            AND extent[1] >= -180 AND extent[3] <= 180 AND extent[1] <= extent[3]
            AND extent[2] >= -90 AND extent[4] <= 90 AND extent[2] <= extent[4]
        )
    )
);

CREATE OR REPLACE FUNCTION $HonuaSchema$.record_external_layer_change(
    changed_layer_id integer, changed_extent double precision[] DEFAULT NULL)
RETURNS bigint
LANGUAGE plpgsql
AS $function$
DECLARE
    next_revision bigint;
BEGIN
    UPDATE $HonuaSchema$.external_layer_revisions
       SET revision = revision + 1
     WHERE layer_id = changed_layer_id AND enabled
     RETURNING revision INTO next_revision;

    IF next_revision IS NULL THEN
        RETURN NULL;
    END IF;

    INSERT INTO $HonuaSchema$.external_layer_changes (layer_id, revision, extent)
    VALUES (changed_layer_id, next_revision, changed_extent);
    RETURN next_revision;
END;
$function$;

REVOKE ALL ON FUNCTION $HonuaSchema$.record_external_layer_change(integer, double precision[]) FROM PUBLIC;
