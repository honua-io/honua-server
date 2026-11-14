-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_notification_schedules (
    source TEXT COLLATE "C" NOT NULL,
    subject_id TEXT COLLATE "C" NOT NULL,
    episode_id UUID NOT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL,
    severity INTEGER NOT NULL CHECK (severity BETWEEN 0 AND 2),
    notice_number INTEGER NOT NULL DEFAULT 0 CHECK (notice_number >= 0),
    next_notice_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    closed_at TIMESTAMPTZ,
    revision BIGINT NOT NULL DEFAULT 0 CHECK (revision >= 0),
    PRIMARY KEY (source, subject_id)
);

CREATE INDEX IF NOT EXISTS idx_ops_notification_schedules_due
    ON $HonuaSchema$.ops_notification_schedules (next_notice_at)
    WHERE closed_at IS NULL;
