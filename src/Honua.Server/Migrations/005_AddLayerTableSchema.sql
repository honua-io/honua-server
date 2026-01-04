-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add table_schema metadata for layer table resolution.
ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

COMMENT ON COLUMN honua.layers.table_schema IS 'Schema name containing the layer table';
