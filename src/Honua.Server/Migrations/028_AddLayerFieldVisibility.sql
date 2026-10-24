-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Persist operator-managed field visibility for public protocol output.

ALTER TABLE honua.layer_fields
    ADD COLUMN IF NOT EXISTS hidden BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN honua.layer_fields.hidden IS 'Operator-managed flag that omits the field from public protocol metadata and feature output';
