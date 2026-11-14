-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Operational counters do not advance the configuration generation.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.rate_limit_counters (
    counter_key TEXT COLLATE "C" PRIMARY KEY,
    request_count BIGINT NOT NULL CHECK (request_count > 0 AND request_count <= 2147483648),
    window_reset TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rate_limit_counters_expiry
    ON $HonuaSchema$.rate_limit_counters (window_reset);
