-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Package drafts (ADR-0082 row A6).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.package_drafts (
    kind       TEXT COLLATE "C" NOT NULL CHECK (kind IN ('map', 'app')),
    draft_id   TEXT COLLATE "C" NOT NULL CHECK (length(draft_id) BETWEEN 1 AND 512),
    owner_id   TEXT NOT NULL,
    written_at TIMESTAMPTZ NOT NULL,
    record     JSON NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (kind, draft_id)
);
CREATE INDEX IF NOT EXISTS idx_package_drafts_written
    ON $HonuaSchema$.package_drafts (kind, written_at, draft_id);
CREATE INDEX IF NOT EXISTS idx_package_drafts_expires
    ON $HonuaSchema$.package_drafts (expires_at);

COMMENT ON TABLE $HonuaSchema$.package_drafts IS 'Map and app package drafts readable by their owner on any node until expiry.';
