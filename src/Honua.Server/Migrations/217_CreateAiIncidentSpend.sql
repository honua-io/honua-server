-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
-- honua:compatibility-review reason=additive nullable accounting identity and opt-in incident cap; prior readers remain compatible and writers without a trusted binding are refused once enabled; policy changes require stopped and drained admission

ALTER TABLE $HonuaSchema$.ai_financial_invocations ADD COLUMN IF NOT EXISTS incident_authority TEXT;
ALTER TABLE $HonuaSchema$.ai_financial_invocations ADD COLUMN IF NOT EXISTS incident_episode UUID;
ALTER TABLE $HonuaSchema$.ai_financial_invocations ADD COLUMN IF NOT EXISTS incident_version BIGINT;
CREATE INDEX IF NOT EXISTS idx_ai_financial_incident ON $HonuaSchema$.ai_financial_invocations
    (deployment_id, incident_authority, incident_episode) WHERE incident_episode IS NOT NULL;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_incident_notices (
    deployment_id TEXT NOT NULL REFERENCES $HonuaSchema$.ai_execution_policies(deployment_id),
    authority_scope TEXT NOT NULL,
    episode_id UUID NOT NULL,
    charged_tokens NUMERIC NOT NULL CHECK (charged_tokens >= 0),
    charged_money NUMERIC NOT NULL CHECK (charged_money >= 0),
    token_limit BIGINT NOT NULL CHECK (token_limit > 0),
    money_limit BIGINT NOT NULL CHECK (money_limit > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    delivered_at TIMESTAMPTZ,
    PRIMARY KEY (deployment_id, authority_scope, episode_id)
);
CREATE INDEX IF NOT EXISTS idx_ai_incident_notices_pending ON $HonuaSchema$.ai_incident_notices
    (created_at, deployment_id) WHERE delivered_at IS NULL;

CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_incident_policy() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    old_financial JSONB;
    financial JSONB;
    cap JSONB;
    dimension TEXT;
    bindings JSONB;
    binding JSONB;
BEGIN
    IF TG_OP <> 'INSERT' THEN old_financial := OLD.policy_json->'Financial'; END IF;
    IF TG_OP = 'DELETE' THEN
        IF COALESCE(old_financial->'Incident', 'null'::jsonb) <> 'null'::jsonb THEN
            RAISE EXCEPTION 'Incident policy deletion is forbidden' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    financial := NEW.policy_json->'Financial';
    cap := financial->'Incident';
    IF COALESCE(cap, 'null'::jsonb) = 'null'::jsonb THEN
        IF COALESCE(old_financial->'Incident', 'null'::jsonb) <> 'null'::jsonb THEN
            RAISE EXCEPTION 'Incident cap removal is forbidden' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF jsonb_typeof(cap) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'Incident limits must be an object' USING ERRCODE = '23514';
    END IF;
    FOREACH dimension IN ARRAY ARRAY['Tokens', 'Money'] LOOP
        IF jsonb_typeof(cap->dimension) IS DISTINCT FROM 'number'
            OR COALESCE(cap->>dimension, '') !~ '^[1-9][0-9]{0,15}$' THEN
            RAISE EXCEPTION 'Incident limits must be positive bounded integers' USING ERRCODE = '23514';
        END IF;
        IF (cap->>dimension)::numeric > 1000000000000000 THEN
            RAISE EXCEPTION 'Incident limit exceeds its bounded representation' USING ERRCODE = '23514';
        END IF;
    END LOOP;
    bindings := COALESCE(NULLIF(financial->'IncidentWorkflows', 'null'::jsonb), '[]'::jsonb);
    IF jsonb_typeof(bindings) IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'Incident bindings must be an array' USING ERRCODE = '23514';
    END IF;
    IF jsonb_array_length(bindings) > 64 THEN
        RAISE EXCEPTION 'Too many incident bindings' USING ERRCODE = '23514';
    END IF;
    FOR binding IN SELECT value FROM jsonb_array_elements(bindings) LOOP
        IF jsonb_typeof(binding) IS DISTINCT FROM 'object'
            OR COALESCE(binding->>'Workflow', '') !~ '^job:.+'
            OR length(binding->>'Workflow') > 4096
            OR binding->>'Workflow' ~ '[[:cntrl:]]'
            OR binding->>'AuthorityScope' ~ '[[:cntrl:]]'
            OR COALESCE(octet_length(binding->>'AuthorityScope'), 0) NOT BETWEEN 1 AND 256
            OR COALESCE(binding->>'EpisodeId', '') !~ '^[0-9a-fA-F-]{36}$'
            OR COALESCE(binding->>'LifecycleVersion', '') !~ '^[1-9][0-9]{0,18}$' THEN
            RAISE EXCEPTION 'Invalid incident workflow binding' USING ERRCODE = '23514';
        END IF;
        IF (binding->>'EpisodeId')::uuid = '00000000-0000-0000-0000-000000000000'::uuid
            OR (binding->>'LifecycleVersion')::numeric > 9223372036854775807 THEN
            RAISE EXCEPTION 'Invalid incident episode identity' USING ERRCODE = '23514';
        END IF;
    END LOOP;
    IF (SELECT count(*) <> count(DISTINCT value->>'Workflow') FROM jsonb_array_elements(bindings)) THEN
        RAISE EXCEPTION 'Duplicate incident workflow binding' USING ERRCODE = '23514';
    END IF;
    IF cap IS DISTINCT FROM (old_financial->'Incident')
        OR (financial->'IncidentWorkflows') IS DISTINCT FROM (old_financial->'IncidentWorkflows') THEN
        IF NOT NEW.stopped OR (TG_OP = 'UPDATE' AND (NOT OLD.stopped OR NEW.epoch <= OLD.epoch))
            OR EXISTS (SELECT 1 FROM $HonuaSchema$.ai_execution_attempts a
                WHERE a.deployment_id = NEW.deployment_id AND a.released_at IS NULL
                    AND a.lease_expires_at > clock_timestamp()) THEN
            RAISE EXCEPTION 'Incident policy changes require stopped and drained admission' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_incident_reservation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    financial JSONB;
    cap JSONB;
    tokens NUMERIC;
    money NUMERIC;
BEGIN
    SELECT policy_json->'Financial' INTO financial FROM $HonuaSchema$.ai_execution_policies
        WHERE deployment_id = NEW.deployment_id FOR UPDATE;
    cap := financial->'Incident';
    IF TG_OP = 'UPDATE' AND (NEW.incident_authority, NEW.incident_episode, NEW.incident_version)
        IS DISTINCT FROM (OLD.incident_authority, OLD.incident_episode, OLD.incident_version) THEN
        RAISE EXCEPTION 'Incident accounting identity is immutable' USING ERRCODE = '23514';
    END IF;
    IF COALESCE(cap, 'null'::jsonb) = 'null'::jsonb THEN RETURN NEW; END IF;
    IF TG_OP = 'UPDATE' AND NOT (OLD.state = 'reserved' AND NEW.state = 'send-committed') THEN RETURN NEW; END IF;
    IF NOT EXISTS (SELECT 1 FROM jsonb_array_elements(financial->'IncidentWorkflows') b
        WHERE encode(sha256(convert_to(b->>'Workflow', 'UTF8')), 'hex') = NEW.workflow_key
            AND b->>'AuthorityScope' = NEW.incident_authority
            AND (b->>'EpisodeId')::uuid = NEW.incident_episode
            AND (b->>'LifecycleVersion')::bigint = NEW.incident_version) THEN
        RAISE EXCEPTION 'Trusted incident workflow binding is required' USING ERRCODE = '23514';
    END IF;
    PERFORM 1 FROM $HonuaSchema$.ops_incident_episodes e
        WHERE e.authority_scope = NEW.incident_authority AND e.episode_id = NEW.incident_episode
            AND e.lifecycle_version = NEW.incident_version AND e.state = 0 AND e.closed_at IS NULL
            AND e.observed_at >= clock_timestamp() - interval '5 minutes'
            AND e.observed_at <= clock_timestamp() + interval '30 seconds' FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Current incident episode is required' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN RETURN NEW; END IF;
    SELECT COALESCE(sum(charged_tokens::numeric), 0), COALESCE(sum(charged_money::numeric), 0)
        INTO tokens, money FROM $HonuaSchema$.ai_financial_invocations
        WHERE deployment_id = NEW.deployment_id AND incident_authority = NEW.incident_authority
            AND incident_episode = NEW.incident_episode;
    IF tokens + NEW.charged_tokens::numeric > (cap->>'Tokens')::numeric
        OR money + NEW.charged_money::numeric > (cap->>'Money')::numeric THEN
        RAISE EXCEPTION 'Incident spend cap reached' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.enqueue_ai_incident_notice(deployment TEXT) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE cap JSONB;
BEGIN
    SELECT policy_json->'Financial'->'Incident' INTO cap FROM $HonuaSchema$.ai_execution_policies
        WHERE deployment_id = deployment FOR UPDATE;
    IF COALESCE(cap, 'null'::jsonb) = 'null'::jsonb THEN RETURN; END IF;
    INSERT INTO $HonuaSchema$.ai_incident_notices
        (deployment_id, authority_scope, episode_id, charged_tokens, charged_money, token_limit, money_limit)
    SELECT deployment, incident_authority, incident_episode, sum(charged_tokens::numeric), sum(charged_money::numeric),
        (cap->>'Tokens')::bigint, (cap->>'Money')::bigint
    FROM $HonuaSchema$.ai_financial_invocations
    WHERE deployment_id = deployment AND incident_episode IS NOT NULL
    GROUP BY incident_authority, incident_episode
    HAVING sum(charged_tokens::numeric) * 5 >= (cap->>'Tokens')::numeric * 4
        OR sum(charged_money::numeric) * 5 >= (cap->>'Money')::numeric * 4
    ON CONFLICT (deployment_id, authority_scope, episode_id) DO NOTHING;
END;
$$;
CREATE OR REPLACE FUNCTION $HonuaSchema$.notice_ai_incident_reservation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM $HonuaSchema$.enqueue_ai_incident_notice(NEW.deployment_id);
    RETURN NEW;
END;
$$;
CREATE OR REPLACE FUNCTION $HonuaSchema$.notice_ai_incident_policy() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' OR (NEW.policy_json->'Financial'->'Incident') IS DISTINCT FROM (OLD.policy_json->'Financial'->'Incident') THEN
        PERFORM $HonuaSchema$.enqueue_ai_incident_notice(NEW.deployment_id);
    END IF;
    RETURN NEW;
END;
$$;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'guard_ai_incident_policy'
        AND tgrelid = '$HonuaSchema$.ai_execution_policies'::regclass) THEN
        CREATE TRIGGER guard_ai_incident_policy BEFORE INSERT OR UPDATE OR DELETE
            ON $HonuaSchema$.ai_execution_policies FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_incident_policy();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'guard_ai_incident_reservation'
        AND tgrelid = '$HonuaSchema$.ai_financial_invocations'::regclass) THEN
        CREATE TRIGGER guard_ai_incident_reservation BEFORE INSERT OR UPDATE
            ON $HonuaSchema$.ai_financial_invocations FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_incident_reservation();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'notice_ai_incident_reservation'
        AND tgrelid = '$HonuaSchema$.ai_financial_invocations'::regclass) THEN
        CREATE TRIGGER notice_ai_incident_reservation AFTER INSERT OR UPDATE
            ON $HonuaSchema$.ai_financial_invocations FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.notice_ai_incident_reservation();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'notice_ai_incident_policy'
        AND tgrelid = '$HonuaSchema$.ai_execution_policies'::regclass) THEN
        CREATE TRIGGER notice_ai_incident_policy AFTER INSERT OR UPDATE
            ON $HonuaSchema$.ai_execution_policies FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.notice_ai_incident_policy();
    END IF;
END $$;
