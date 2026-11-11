-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- The retention sweep deletes expired approved-operation credentials by this index (ADR-0082 row B12).
CREATE INDEX IF NOT EXISTS idx_admin_api_keys_expires
    ON $HonuaSchema$.admin_api_keys (expires_at) WHERE expires_at IS NOT NULL;
