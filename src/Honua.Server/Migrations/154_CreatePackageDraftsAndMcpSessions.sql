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

-- MCP sessions and the requests running in them (ADR-0082 rows A7 to A9).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.mcp_sessions (
    session_id    TEXT COLLATE "C" PRIMARY KEY CHECK (length(session_id) BETWEEN 1 AND 512),
    principal_key TEXT NOT NULL,
    record        JSON NOT NULL,
    expires_at    TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mcp_sessions_expires
    ON $HonuaSchema$.mcp_sessions (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.mcp_requests (
    session_id    TEXT COLLATE "C" NOT NULL REFERENCES $HonuaSchema$.mcp_sessions (session_id) ON DELETE CASCADE,
    request_key   TEXT COLLATE "C" NOT NULL,
    principal_key TEXT NOT NULL,
    generation    TEXT COLLATE "C" NOT NULL,
    cancelled     BOOLEAN NOT NULL DEFAULT false,
    expires_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (session_id, request_key)
);
CREATE INDEX IF NOT EXISTS idx_mcp_requests_principal
    ON $HonuaSchema$.mcp_requests (principal_key, expires_at);

COMMENT ON TABLE $HonuaSchema$.mcp_sessions IS 'MCP sessions every node accepts until idle expiry.';
COMMENT ON TABLE $HonuaSchema$.mcp_requests IS 'MCP requests running on any node, with their cancellation flag.';

-- Admits one request. The transaction lock on the principal serializes its registers and the row
-- lock holds the session against removal; each statement below takes a fresh snapshot, so the
-- counts include every request a previous holder committed.
CREATE OR REPLACE FUNCTION $HonuaSchema$.mcp_register_request(
    p_session_id TEXT, p_principal_key TEXT, p_request_key TEXT, p_generation TEXT,
    p_lifetime INTERVAL, p_per_session INTEGER, p_per_principal INTEGER) RETURNS TEXT
LANGUAGE plpgsql VOLATILE AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('mcp_requests:' || p_principal_key, 0));
    PERFORM 1 FROM $HonuaSchema$.mcp_sessions
        WHERE session_id = p_session_id AND principal_key = p_principal_key AND expires_at > now()
        FOR UPDATE;
    IF NOT FOUND THEN
        RETURN 'missing';
    END IF;
    DELETE FROM $HonuaSchema$.mcp_requests WHERE session_id = p_session_id AND expires_at <= now();
    IF EXISTS (SELECT 1 FROM $HonuaSchema$.mcp_requests
               WHERE session_id = p_session_id AND request_key = p_request_key) THEN
        RETURN 'duplicate';
    END IF;
    IF (SELECT count(*) FROM $HonuaSchema$.mcp_requests WHERE session_id = p_session_id) >= p_per_session
        OR (SELECT count(*) FROM $HonuaSchema$.mcp_requests AS request
            JOIN $HonuaSchema$.mcp_sessions AS session ON session.session_id = request.session_id
            WHERE request.principal_key = p_principal_key AND request.expires_at > now()
                AND session.expires_at > now()) >= p_per_principal THEN
        RETURN 'capacity';
    END IF;
    INSERT INTO $HonuaSchema$.mcp_requests (session_id, request_key, principal_key, generation, expires_at)
    VALUES (p_session_id, p_request_key, p_principal_key, p_generation, now() + p_lifetime);
    RETURN 'active';
END;
$$;
