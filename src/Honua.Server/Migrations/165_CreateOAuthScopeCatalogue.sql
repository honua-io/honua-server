-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.oauth_scope_catalogue (
    scope_key TEXT COLLATE "C" PRIMARY KEY,
    body JSONB NOT NULL
);
