-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- A worker can die after importing rows but before publishing its terminal progress.
-- This record commits with the rows and prevents a replacement worker from replaying them.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.remote_import_commit_receipt (
    job_id TEXT COLLATE "C" PRIMARY KEY,
    schema_name TEXT NOT NULL,
    table_name TEXT NOT NULL,
    source_service_url TEXT NOT NULL,
    source_layer_id INTEGER NOT NULL,
    feature_count INTEGER NOT NULL CHECK (feature_count >= 0),
    failed_features INTEGER NOT NULL CHECK (failed_features >= 0),
    committed_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);
