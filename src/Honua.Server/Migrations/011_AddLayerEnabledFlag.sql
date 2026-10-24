-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Add enabled flag to layer configuration
ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN honua.layers.enabled IS 'Whether the layer is enabled for API exposure';
