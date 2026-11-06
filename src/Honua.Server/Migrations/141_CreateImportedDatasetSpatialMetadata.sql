-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Imported dataset facts exist before publication. Relation OID and geometry attnum prevent
-- stale name-based metadata from attaching to an externally dropped/recreated relation/column.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.imported_dataset_spatial_metadata (
    schema_name text NOT NULL,
    table_name text NOT NULL,
    geometry_column text NOT NULL,
    relation_oid oid NOT NULL,
    geometry_attnum smallint NOT NULL CHECK (geometry_attnum > 0),
    storage_srid integer NOT NULL CHECK (storage_srid > 0),
    coordinate_epoch double precision,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (schema_name, table_name, geometry_column),
    CHECK (char_length(schema_name) BETWEEN 1 AND 63),
    CHECK (char_length(table_name) BETWEEN 1 AND 63),
    CHECK (char_length(geometry_column) BETWEEN 1 AND 63),
    CHECK (coordinate_epoch IS NULL OR
        (coordinate_epoch > '-Infinity'::double precision AND coordinate_epoch < 'Infinity'::double precision))
);

COMMENT ON TABLE $HonuaSchema$.imported_dataset_spatial_metadata IS
    'Identity-bound import storage CRS and epoch; written with import rows and projected into existing Metadata v2 on publication.';
