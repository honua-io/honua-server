CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_incident_episodes (
    authority_scope TEXT NOT NULL CHECK (octet_length(authority_scope) BETWEEN 1 AND 256),
    episode_id UUID NOT NULL,
    finding_id TEXT NOT NULL CHECK (octet_length(finding_id) BETWEEN 1 AND 256),
    lifecycle_version BIGINT NOT NULL CHECK (lifecycle_version > 0),
    state SMALLINT NOT NULL CHECK (state BETWEEN 0 AND 2),
    started_at TIMESTAMPTZ NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL,
    closed_at TIMESTAMPTZ,
    PRIMARY KEY (authority_scope, episode_id),
    CHECK (observed_at >= started_at),
    CHECK ((state = 1) = (closed_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_ops_incident_episodes_finding
    ON $HonuaSchema$.ops_incident_episodes (authority_scope, finding_id, started_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_incident_episode_cursors (
    authority_scope TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL DEFAULT '-infinity',
    episode_id UUID,
    PRIMARY KEY (authority_scope, finding_id),
    FOREIGN KEY (authority_scope, episode_id)
        REFERENCES $HonuaSchema$.ops_incident_episodes (authority_scope, episode_id)
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.ops_incident_episode_receipts (
    authority_scope TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    observation_id TEXT NOT NULL CHECK (octet_length(observation_id) BETWEEN 1 AND 256),
    episode JSONB,
    PRIMARY KEY (authority_scope, finding_id, observation_id),
    FOREIGN KEY (authority_scope, finding_id)
        REFERENCES $HonuaSchema$.ops_incident_episode_cursors (authority_scope, finding_id)
);

-- The per-finding cursor serializes observations across hosts within one statement transaction.
CREATE OR REPLACE FUNCTION $HonuaSchema$.observe_ops_incident_episode(
    p_authority TEXT, p_finding TEXT, p_observation TEXT, p_observed TIMESTAMPTZ, p_state SMALLINT)
RETURNS SETOF $HonuaSchema$.ops_incident_episodes
LANGUAGE plpgsql AS $$
DECLARE
    v_cursor $HonuaSchema$.ops_incident_episode_cursors%ROWTYPE;
    v_episode $HonuaSchema$.ops_incident_episodes%ROWTYPE;
    v_receipt JSONB;
BEGIN
    IF p_observed > now() + interval '30 seconds' OR p_state NOT BETWEEN 0 AND 2 THEN
        RAISE EXCEPTION 'Invalid incident observation';
    END IF;
    INSERT INTO $HonuaSchema$.ops_incident_episode_cursors (authority_scope, finding_id)
        VALUES (p_authority, p_finding) ON CONFLICT DO NOTHING;
    SELECT * INTO STRICT v_cursor FROM $HonuaSchema$.ops_incident_episode_cursors
        WHERE authority_scope = p_authority AND finding_id = p_finding FOR UPDATE;
    SELECT episode INTO v_receipt FROM $HonuaSchema$.ops_incident_episode_receipts
        WHERE authority_scope = p_authority AND finding_id = p_finding AND observation_id = p_observation;
    IF FOUND THEN
        IF v_receipt IS NOT NULL THEN
            SELECT * INTO v_episode FROM jsonb_populate_record(
                NULL::$HonuaSchema$.ops_incident_episodes, v_receipt);
            RETURN NEXT v_episode;
        END IF;
        RETURN;
    END IF;
    IF v_cursor.episode_id IS NOT NULL THEN
        SELECT * INTO STRICT v_episode FROM $HonuaSchema$.ops_incident_episodes
            WHERE authority_scope = p_authority AND episode_id = v_cursor.episode_id;
    END IF;
    IF p_observed > v_cursor.observed_at THEN
        IF p_state = 0 AND (v_episode.episode_id IS NULL OR v_episode.closed_at IS NOT NULL) THEN
            INSERT INTO $HonuaSchema$.ops_incident_episodes
                (authority_scope, episode_id, finding_id, lifecycle_version, state, started_at, observed_at)
                VALUES (p_authority, gen_random_uuid(), p_finding, 1, 0, p_observed, p_observed)
                RETURNING * INTO v_episode;
        ELSIF v_episode.episode_id IS NOT NULL AND v_episode.closed_at IS NULL THEN
            UPDATE $HonuaSchema$.ops_incident_episodes
                SET lifecycle_version = lifecycle_version + CASE WHEN state = p_state THEN 0 ELSE 1 END,
                    state = p_state, observed_at = p_observed,
                    closed_at = CASE WHEN p_state = 1 THEN p_observed ELSE NULL END
                WHERE authority_scope = p_authority AND episode_id = v_episode.episode_id
                RETURNING * INTO v_episode;
        END IF;
        UPDATE $HonuaSchema$.ops_incident_episode_cursors
            SET observed_at = p_observed, episode_id = v_episode.episode_id
            WHERE authority_scope = p_authority AND finding_id = p_finding;
    END IF;
    INSERT INTO $HonuaSchema$.ops_incident_episode_receipts
        (authority_scope, finding_id, observation_id, episode)
        VALUES (p_authority, p_finding, p_observation,
            CASE WHEN v_episode.episode_id IS NULL THEN NULL ELSE to_jsonb(v_episode) END);
    IF v_episode.episode_id IS NOT NULL THEN
        RETURN NEXT v_episode;
    END IF;
END;
$$;
