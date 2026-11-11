-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Branch-version reconcile and post jobs (ADR-0082 row D5). stage is 0 pending, 1 running and
-- 2 finished; a save only ever raises it.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.version_jobs (
    job_id     UUID PRIMARY KEY,
    stage      SMALLINT NOT NULL CHECK (stage BETWEEN 0 AND 2),
    record     JSON NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_version_jobs_expires
    ON $HonuaSchema$.version_jobs (expires_at);

-- Resume points of tile-cache seed and warm generations (ADR-0082 row D6).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.tile_generation_checkpoints (
    generation_id TEXT COLLATE "C" PRIMARY KEY,
    record        JSON NOT NULL,
    expires_at    TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tile_generation_checkpoints_expires
    ON $HonuaSchema$.tile_generation_checkpoints (expires_at);

COMMENT ON TABLE $HonuaSchema$.version_jobs IS 'Branch-version reconcile and post jobs; stage is derived from the status in record and only ever rises.';
COMMENT ON TABLE $HonuaSchema$.tile_generation_checkpoints IS 'The last completed metatile block of each tile-cache generation, from which a retry resumes.';
