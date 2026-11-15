-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.wfs_stored_queries (
    scope TEXT COLLATE "C" NOT NULL,
    query_id TEXT COLLATE "C" NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    definition JSON NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_wfs_stored_queries_scope_query
    ON $HonuaSchema$.wfs_stored_queries (scope, query_id);

COMMENT ON TABLE $HonuaSchema$.wfs_stored_queries IS
    'WFS managed stored queries; query_id uses .NET invariant uppercase normalization for case-insensitive identity.';
