-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

ALTER TABLE honua.rbac_rls_policies
    ADD COLUMN IF NOT EXISTS filter_expression TEXT,
    ADD COLUMN IF NOT EXISTS area_geometry TEXT;
