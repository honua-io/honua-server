-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Schedule retries independently of historical physical attachment authorities.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_cleanup_retries (
    cleanup_authority TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    next_attempt_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (cleanup_authority, storage_path)
);
