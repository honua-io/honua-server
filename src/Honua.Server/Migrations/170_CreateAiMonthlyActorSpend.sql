-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
-- honua:compatibility-review reason=additive table, functions and triggers; caps are opt-in and need stopped/drained admission; until a cap is set the triggers do nothing, so the prior server reads and writes unchanged; once set, a prior server's policy write that drops the cap is refused rather than applied

CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_monthly_actor_notices (
    deployment_id TEXT NOT NULL REFERENCES $HonuaSchema$.ai_execution_policies(deployment_id),
    actor_key TEXT NOT NULL CHECK (actor_key ~ '^[0-9a-f]{64}$'),
    month_start TIMESTAMPTZ NOT NULL,
    charged_tokens NUMERIC NOT NULL CHECK (charged_tokens >= 0),
    charged_money NUMERIC NOT NULL CHECK (charged_money >= 0),
    token_limit BIGINT NOT NULL CHECK (token_limit > 0),
    money_limit BIGINT NOT NULL CHECK (money_limit > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    delivered_at TIMESTAMPTZ,
    PRIMARY KEY (deployment_id, actor_key, month_start)
);
CREATE INDEX IF NOT EXISTS idx_ai_monthly_actor_notices_pending
    ON $HonuaSchema$.ai_monthly_actor_notices (created_at, deployment_id, actor_key)
    WHERE delivered_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_ai_financial_invocations_monthly_actor
    ON $HonuaSchema$.ai_financial_invocations (deployment_id, actor_key, reserved_at);
COMMENT ON TABLE $HonuaSchema$.ai_monthly_actor_notices IS 'Durable monthly actor threshold outbox. Delivery state and retained liabilities survive migration replay.';

CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_monthly_actor_policy() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    old_cap JSONB;
    new_cap JSONB;
    dimension TEXT;
BEGIN
    IF TG_OP <> 'INSERT' THEN old_cap := OLD.policy_json->'Financial'->'MonthlyActor'; END IF;
    IF TG_OP = 'DELETE' THEN
        IF old_cap IS NOT NULL AND old_cap <> 'null'::jsonb THEN
            RAISE EXCEPTION 'Monthly actor policy deletion is forbidden' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    new_cap := NEW.policy_json->'Financial'->'MonthlyActor';
    IF new_cap IS NULL OR new_cap = 'null'::jsonb THEN
        IF old_cap IS NOT NULL AND old_cap <> 'null'::jsonb THEN
            RAISE EXCEPTION 'Monthly actor cap removal is forbidden' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF jsonb_typeof(new_cap) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'Monthly actor limits must be an object' USING ERRCODE = '23514';
    END IF;
    FOREACH dimension IN ARRAY ARRAY['Tokens', 'Money'] LOOP
        IF jsonb_typeof(new_cap->dimension) IS DISTINCT FROM 'number'
            OR COALESCE(new_cap->>dimension, '') !~ '^[1-9][0-9]{0,15}$' THEN
            RAISE EXCEPTION 'Monthly actor limits must be positive bounded integers' USING ERRCODE = '23514';
        END IF;
        IF (new_cap->>dimension)::numeric > 1000000000000000 THEN
            RAISE EXCEPTION 'Monthly actor limit exceeds its bounded representation' USING ERRCODE = '23514';
        END IF;
    END LOOP;
    IF new_cap IS DISTINCT FROM old_cap THEN
        IF NOT NEW.stopped OR (TG_OP = 'UPDATE' AND (NOT OLD.stopped OR NEW.epoch <= OLD.epoch))
            OR EXISTS (SELECT 1 FROM $HonuaSchema$.ai_execution_attempts a
                WHERE a.deployment_id = NEW.deployment_id AND a.released_at IS NULL
                    AND a.lease_expires_at > clock_timestamp()) THEN
            RAISE EXCEPTION 'Monthly actor cap changes require stopped and drained admission' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

-- Every financial writer serializes on the same policy row, including legacy binaries.
-- Settlement may exceed the cap: retaining that debt must succeed and stop the next reservation.
CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_monthly_actor_reservation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    cap JSONB;
    current_month TIMESTAMPTZ := date_trunc('month', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
    next_month TIMESTAMPTZ := ((current_month AT TIME ZONE 'UTC') + INTERVAL '1 month') AT TIME ZONE 'UTC';
    tokens NUMERIC;
    money NUMERIC;
BEGIN
    SELECT policy_json->'Financial'->'MonthlyActor' INTO cap
        FROM $HonuaSchema$.ai_execution_policies WHERE deployment_id = NEW.deployment_id FOR UPDATE;
    IF cap IS NULL OR cap = 'null'::jsonb OR TG_OP <> 'INSERT' THEN RETURN NEW; END IF;
    SELECT COALESCE(sum(f.charged_tokens::numeric), 0), COALESCE(sum(f.charged_money::numeric), 0)
        INTO tokens, money FROM $HonuaSchema$.ai_financial_invocations f
        WHERE f.deployment_id = NEW.deployment_id AND f.actor_key = NEW.actor_key
            AND (f.state <> 'settled' OR (f.reserved_at >= current_month AND f.reserved_at < next_month));
    IF tokens + NEW.charged_tokens::numeric > (cap->>'Tokens')::numeric
        OR money + NEW.charged_money::numeric > (cap->>'Money')::numeric THEN
        RAISE EXCEPTION 'Monthly actor spend cap reached' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.enqueue_ai_monthly_actor_notice(deployment TEXT, actor TEXT) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    cap JSONB;
    current_month TIMESTAMPTZ := date_trunc('month', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
    next_month TIMESTAMPTZ := ((current_month AT TIME ZONE 'UTC') + INTERVAL '1 month') AT TIME ZONE 'UTC';
BEGIN
    SELECT policy_json->'Financial'->'MonthlyActor' INTO cap
        FROM $HonuaSchema$.ai_execution_policies WHERE deployment_id = deployment FOR UPDATE;
    IF cap IS NULL OR cap = 'null'::jsonb THEN RETURN; END IF;
    INSERT INTO $HonuaSchema$.ai_monthly_actor_notices
        (deployment_id, actor_key, month_start, charged_tokens, charged_money, token_limit, money_limit)
    SELECT deployment, f.actor_key, current_month, sum(f.charged_tokens::numeric), sum(f.charged_money::numeric),
        (cap->>'Tokens')::bigint, (cap->>'Money')::bigint
    FROM $HonuaSchema$.ai_financial_invocations f
    WHERE f.deployment_id = deployment AND (actor IS NULL OR f.actor_key = actor)
        AND (f.state <> 'settled' OR (f.reserved_at >= current_month AND f.reserved_at < next_month))
    GROUP BY f.actor_key
    HAVING sum(f.charged_tokens::numeric) * 5 >= (cap->>'Tokens')::numeric * 4
        OR sum(f.charged_money::numeric) * 5 >= (cap->>'Money')::numeric * 4
    ON CONFLICT (deployment_id, actor_key, month_start) DO NOTHING;
END;
$$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.notice_ai_monthly_actor_reservation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM $HonuaSchema$.enqueue_ai_monthly_actor_notice(NEW.deployment_id, NEW.actor_key);
    RETURN NEW;
END;
$$;
CREATE OR REPLACE FUNCTION $HonuaSchema$.notice_ai_monthly_actor_policy() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' OR (NEW.policy_json->'Financial'->'MonthlyActor') IS DISTINCT FROM (OLD.policy_json->'Financial'->'MonthlyActor') THEN
        PERFORM $HonuaSchema$.enqueue_ai_monthly_actor_notice(NEW.deployment_id, NULL);
    END IF;
    RETURN NEW;
END;
$$;

-- Keep identities independent of migration 142 so its replay cannot replace these fences.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'guard_ai_monthly_actor_policy'
        AND tgrelid = '$HonuaSchema$.ai_execution_policies'::regclass) THEN
        CREATE TRIGGER guard_ai_monthly_actor_policy BEFORE INSERT OR UPDATE OR DELETE
            ON $HonuaSchema$.ai_execution_policies FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_monthly_actor_policy();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'guard_ai_monthly_actor_reservation'
        AND tgrelid = '$HonuaSchema$.ai_financial_invocations'::regclass) THEN
        CREATE TRIGGER guard_ai_monthly_actor_reservation BEFORE INSERT OR UPDATE
            ON $HonuaSchema$.ai_financial_invocations FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_monthly_actor_reservation();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'notice_ai_monthly_actor_reservation'
        AND tgrelid = '$HonuaSchema$.ai_financial_invocations'::regclass) THEN
        CREATE TRIGGER notice_ai_monthly_actor_reservation AFTER INSERT OR UPDATE
            ON $HonuaSchema$.ai_financial_invocations FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.notice_ai_monthly_actor_reservation();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'notice_ai_monthly_actor_policy'
        AND tgrelid = '$HonuaSchema$.ai_execution_policies'::regclass) THEN
        CREATE TRIGGER notice_ai_monthly_actor_policy AFTER INSERT OR UPDATE
            ON $HonuaSchema$.ai_execution_policies FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.notice_ai_monthly_actor_policy();
    END IF;
END $$;
