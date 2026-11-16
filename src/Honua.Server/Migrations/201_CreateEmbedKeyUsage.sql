-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Embed key use, shared by every task: the request count of the current rate window and the last use.
-- The keys themselves are configuration documents; use is not configuration and advances no generation.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.embed_key_usage (
    key_id            UUID PRIMARY KEY,
    window_started_at TIMESTAMPTZ NOT NULL,
    request_count     INTEGER NOT NULL CHECK (request_count > 0),
    last_used_at      TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE $HonuaSchema$.embed_key_usage IS 'Per embed key: requests counted in the current rate window and the time of the last request, across all tasks.';
