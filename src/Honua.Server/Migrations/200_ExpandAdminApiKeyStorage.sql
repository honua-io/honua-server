-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Expand-only: existing writers keep legacy hashes and readers select explicit columns.
-- Salted writes must wait until all supported reader versions understand this column.
ALTER TABLE $HonuaSchema$.admin_api_keys
    ADD COLUMN IF NOT EXISTS key_salt BYTEA;

CREATE INDEX IF NOT EXISTS idx_admin_api_keys_salted_prefix
    ON $HonuaSchema$.admin_api_keys (key_prefix) WHERE key_salt IS NOT NULL;
