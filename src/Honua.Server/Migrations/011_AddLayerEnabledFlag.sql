-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add enabled flag to layer configuration
ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN honua.layers.enabled IS 'Whether the layer is enabled for API exposure';
