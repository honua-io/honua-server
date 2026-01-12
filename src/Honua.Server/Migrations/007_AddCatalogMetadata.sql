-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add catalog metadata columns for access policies and future extensions.

ALTER TABLE honua.services
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS metadata JSONB;

COMMENT ON COLUMN honua.services.metadata IS 'Catalog metadata (JSON) including access policies';
COMMENT ON COLUMN honua.layers.metadata IS 'Catalog metadata (JSON) including access policies';
