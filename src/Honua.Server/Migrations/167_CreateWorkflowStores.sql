-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_definitions (
    workflow_id TEXT COLLATE "C" PRIMARY KEY,
    record JSONB NOT NULL
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_runs (
    run_id TEXT COLLATE "C" PRIMARY KEY,
    workflow_id TEXT COLLATE "C" NOT NULL,
    active BOOLEAN NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    record JSONB NOT NULL
);
CREATE INDEX IF NOT EXISTS workflow_runs_active ON $HonuaSchema$.workflow_runs (created_at, run_id) WHERE active;
CREATE INDEX IF NOT EXISTS workflow_runs_history ON $HonuaSchema$.workflow_runs (workflow_id, created_at DESC, run_id);
CREATE INDEX IF NOT EXISTS workflow_runs_expiry ON $HonuaSchema$.workflow_runs (expires_at) WHERE NOT active;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_schedule_cursors (
    workflow_id TEXT COLLATE "C" PRIMARY KEY,
    completed_ticks BIGINT,
    pending_ticks BIGINT
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_trigger_cursors (
    workflow_id TEXT COLLATE "C" NOT NULL,
    cursor_kind TEXT COLLATE "C" NOT NULL,
    marker TEXT NOT NULL,
    ordinal BYTEA NOT NULL,
    PRIMARY KEY (workflow_id, cursor_kind)
);
