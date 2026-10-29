-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
-- honua:compatibility-review reason=financial mode is opt-in and stopped/drained; database triggers fence legacy unpaid writers and policy downgrade
-- Independent nonexpiring liabilities: deliberately no foreign key to expiring attempt history.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_financial_invocations (
    deployment_id TEXT NOT NULL REFERENCES $HonuaSchema$.ai_execution_policies(deployment_id),
    attempt_id UUID NOT NULL,
    generation_id UUID NOT NULL,
    grant_epoch BIGINT NOT NULL CHECK (grant_epoch > 0),
    deadline_at TIMESTAMPTZ NOT NULL,
    lease_expires_at TIMESTAMPTZ NOT NULL,
    actor_key TEXT NOT NULL CHECK (actor_key ~ '^[0-9a-f]{64}$'),
    use_case TEXT NOT NULL CHECK (use_case = 'reporting-narrative'),
    workflow_key TEXT CHECK (workflow_key IS NULL OR workflow_key ~ '^[0-9a-f]{64}$'),
    period_start TIMESTAMPTZ NOT NULL,
    reserved_at TIMESTAMPTZ NOT NULL,
    request_digest TEXT NOT NULL CHECK (request_digest ~ '^[0-9a-f]{64}$'),
    provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 128),
    destination_digest TEXT NOT NULL CHECK (destination_digest ~ '^[0-9a-f]{64}$'),
    provider_response_id TEXT CHECK (provider_response_id IS NULL OR length(provider_response_id) BETWEEN 1 AND 256),
    rejected_response_id TEXT CHECK (rejected_response_id IS NULL OR length(rejected_response_id) BETWEEN 1 AND 256),
    unsupported_usage_digest TEXT CHECK (unsupported_usage_digest IS NULL OR unsupported_usage_digest ~ '^[0-9a-f]{64}$'),
    contract_breach_digest TEXT CHECK (contract_breach_digest IS NULL OR contract_breach_digest ~ '^[0-9a-f]{64}$'),
    unrepresentable_usage_digest TEXT CHECK (unrepresentable_usage_digest IS NULL OR unrepresentable_usage_digest ~ '^[0-9a-f]{64}$'),
    basis_digest TEXT NOT NULL CHECK (basis_digest ~ '^[0-9a-f]{64}$'),
    basis_json JSONB NOT NULL CHECK (octet_length(basis_json::text) <= 8192),
    currency TEXT NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    money_scale BIGINT NOT NULL CHECK (money_scale > 0),
    reserved_input BIGINT NOT NULL CHECK (reserved_input > 0),
    reserved_output BIGINT NOT NULL CHECK (reserved_output > 0),
    reserved_money BIGINT NOT NULL CHECK (reserved_money > 0),
    retained_input BIGINT NOT NULL CHECK (retained_input >= reserved_input),
    retained_output BIGINT NOT NULL CHECK (retained_output >= reserved_output),
    charged_tokens BIGINT NOT NULL CHECK (charged_tokens >= 0),
    charged_money BIGINT NOT NULL CHECK (charged_money >= 0),
    state TEXT NOT NULL CHECK (state IN ('reserved', 'send-committed', 'settled', 'overrun', 'conflict')),
    send_committed_at TIMESTAMPTZ,
    usage_digest TEXT CHECK (usage_digest IS NULL OR usage_digest ~ '^[0-9a-f]{64}$'),
    conflict_digest TEXT CHECK (conflict_digest IS NULL OR conflict_digest ~ '^[0-9a-f]{64}$'),
    actual_input BIGINT CHECK (actual_input IS NULL OR actual_input >= 0),
    actual_output BIGINT CHECK (actual_output IS NULL OR actual_output >= 0),
    settled_at TIMESTAMPTZ,
    PRIMARY KEY (deployment_id, attempt_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_ai_financial_response_identity
    ON $HonuaSchema$.ai_financial_invocations (deployment_id, provider, destination_digest, provider_response_id)
    WHERE provider_response_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_ai_financial_invocations_scope
    ON $HonuaSchema$.ai_financial_invocations (deployment_id, period_start, actor_key, use_case, workflow_key);
COMMENT ON TABLE $HonuaSchema$.ai_financial_invocations IS 'Retained model liabilities and original basis; never expire with attempts or leases. No automatic financial retention/compaction is approved.';

-- honua:reader-incompatible reason=financial-mode writer fences require the matching financial admission contract
-- A database-level fence protects financial activation during mixed-version rollout. Old
-- binaries ignore unknown JSON fields; they must not insert unpaid attempts or drop the mode.
CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_financial_policy() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    old_active BOOLEAN := FALSE;
    new_active BOOLEAN := FALSE;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        old_active := OLD.policy_json ? 'Financial' AND OLD.policy_json->'Financial' <> 'null'::jsonb;
    END IF;
    IF TG_OP = 'DELETE' THEN
        IF old_active THEN RAISE EXCEPTION 'Financial policy deletion is forbidden' USING ERRCODE = '23514'; END IF;
        RETURN OLD;
    END IF;
    new_active := NEW.policy_json ? 'Financial' AND NEW.policy_json->'Financial' <> 'null'::jsonb;
    IF octet_length(NEW.policy_json::text) > 8192 THEN
        RAISE EXCEPTION 'AI policy exceeds its bounded representation' USING ERRCODE = '23514';
    END IF;
    IF old_active AND NOT new_active THEN
        RAISE EXCEPTION 'Financial policy downgrade is forbidden' USING ERRCODE = '23514';
    END IF;
    IF old_active AND ((NEW.policy_json->'Financial'->'Currency') IS DISTINCT FROM (OLD.policy_json->'Financial'->'Currency')
        OR (NEW.policy_json->'Financial'->'MoneyScale') IS DISTINCT FROM (OLD.policy_json->'Financial'->'MoneyScale')
        OR (NEW.policy_json->'Financial'->'PeriodAnchorUtc') IS DISTINCT FROM (OLD.policy_json->'Financial'->'PeriodAnchorUtc')
        OR (NEW.policy_json->'Financial'->'PeriodSeconds') IS DISTINCT FROM (OLD.policy_json->'Financial'->'PeriodSeconds')
        OR NEW.generation_id <> OLD.generation_id) THEN
        RAISE EXCEPTION 'Financial accounting identity cannot be reset' USING ERRCODE = '23514';
    END IF;
    IF new_active AND NOT old_active THEN
        IF NOT NEW.stopped OR (TG_OP = 'UPDATE' AND (NOT OLD.stopped OR NEW.epoch <= OLD.epoch))
            OR EXISTS (SELECT 1 FROM $HonuaSchema$.ai_execution_attempts a WHERE a.deployment_id = NEW.deployment_id
                AND a.released_at IS NULL AND a.lease_expires_at > clock_timestamp()) THEN
            RAISE EXCEPTION 'Financial activation requires stopped and drained admission' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
-- Preserve existing trigger identities when independent migration journals replay this script.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger
                   WHERE tgname = 'guard_ai_financial_policy'
                     AND tgrelid = '$HonuaSchema$.ai_execution_policies'::regclass) THEN
        CREATE TRIGGER guard_ai_financial_policy
            BEFORE INSERT OR UPDATE OR DELETE ON $HonuaSchema$.ai_execution_policies
            FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_financial_policy();
    END IF;
END $$;

CREATE OR REPLACE FUNCTION $HonuaSchema$.guard_ai_financial_attempt() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    policy JSONB;
BEGIN
    SELECT policy_json INTO policy FROM $HonuaSchema$.ai_execution_policies
        WHERE deployment_id = NEW.deployment_id FOR UPDATE;
    IF policy ? 'Financial' AND policy->'Financial' <> 'null'::jsonb THEN
        IF NOT EXISTS (SELECT 1 FROM $HonuaSchema$.ai_financial_invocations f
            WHERE f.deployment_id = NEW.deployment_id AND f.attempt_id = NEW.attempt_id
                AND f.generation_id = NEW.generation_id AND f.actor_key = NEW.actor_key
                AND f.use_case = NEW.use_case AND f.workflow_key IS NOT DISTINCT FROM NEW.workflow_key
                AND f.grant_epoch = NEW.grant_epoch AND f.deadline_at = NEW.deadline_at
                AND f.lease_expires_at = NEW.lease_expires_at AND f.state = 'reserved') THEN
            RAISE EXCEPTION 'Financial reservation is required before attempt admission' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger
                   WHERE tgname = 'guard_ai_financial_attempt'
                     AND tgrelid = '$HonuaSchema$.ai_execution_attempts'::regclass) THEN
        CREATE TRIGGER guard_ai_financial_attempt BEFORE INSERT ON $HonuaSchema$.ai_execution_attempts
            FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.guard_ai_financial_attempt();
    END IF;
END $$;
