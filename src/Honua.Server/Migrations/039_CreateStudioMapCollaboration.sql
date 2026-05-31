-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable Studio map collaboration: feature-pinned comment threads and the
-- collaboration activity feed for a map draft/package (#1278, slice 1).
-- The real-time presence/cursor/markup transport is a separate slice and is
-- intentionally not modelled here.

CREATE TABLE IF NOT EXISTS honua.studio_map_comment_threads (
    thread_id     UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    map_id        TEXT        NOT NULL,
    feature_label TEXT        NOT NULL,
    layer_ref     TEXT        NOT NULL,
    anchor_x      DOUBLE PRECISION NOT NULL,
    anchor_y      DOUBLE PRECISION NOT NULL,
    resolved      BOOLEAN     NOT NULL DEFAULT FALSE,
    created_by    TEXT        NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Dominant access path: "list threads for this map, newest activity first."
CREATE INDEX IF NOT EXISTS idx_studio_map_comment_threads_map
    ON honua.studio_map_comment_threads (map_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS honua.studio_map_comment_messages (
    message_id  UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    thread_id   UUID        NOT NULL REFERENCES honua.studio_map_comment_threads(thread_id) ON DELETE CASCADE,
    author_name TEXT        NOT NULL,
    author_id   TEXT        NULL,
    body        TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Messages are always read in thread order, oldest first.
CREATE INDEX IF NOT EXISTS idx_studio_map_comment_messages_thread
    ON honua.studio_map_comment_messages (thread_id, created_at);

CREATE TABLE IF NOT EXISTS honua.studio_map_activity_events (
    event_id   UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    map_id     TEXT        NOT NULL,
    kind       TEXT        NOT NULL,
    actor_name TEXT        NOT NULL,
    actor_id   TEXT        NULL,
    action     TEXT        NOT NULL,
    thread_id  UUID        NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_studio_map_activity_kind CHECK (kind IN (
        'thread-opened','comment-posted','thread-resolved','thread-reopened'))
);

-- Activity feed is read newest-first per map.
CREATE INDEX IF NOT EXISTS idx_studio_map_activity_events_map
    ON honua.studio_map_activity_events (map_id, occurred_at DESC);

COMMENT ON TABLE honua.studio_map_comment_threads IS
    'Feature-pinned comment threads on a Studio map draft/package (#1278, slice 1).';
COMMENT ON TABLE honua.studio_map_activity_events IS
    'Durable collaboration activity feed for a Studio map draft/package (#1278, slice 1).';
