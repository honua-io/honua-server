-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Add table_schema metadata for layer table resolution.
ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

COMMENT ON COLUMN honua.layers.table_schema IS 'Schema name containing the layer table';
