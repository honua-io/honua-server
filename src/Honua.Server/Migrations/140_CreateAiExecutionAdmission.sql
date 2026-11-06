-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
-- Durable AI attempt admission; no startup/bootstrap reset and no provider payloads.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_execution_policies (
    deployment_id TEXT PRIMARY KEY CHECK (length(deployment_id) BETWEEN 1 AND 128),
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    policy_json JSONB NOT NULL,
    generation_id UUID NOT NULL,
    revision BIGINT NOT NULL CHECK (revision > 0),
    epoch BIGINT NOT NULL CHECK (epoch > 0),
    stopped BOOLEAN NOT NULL DEFAULT TRUE,
    not_before TIMESTAMPTZ NOT NULL
);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_execution_attempts (
    deployment_id TEXT NOT NULL REFERENCES $HonuaSchema$.ai_execution_policies(deployment_id),
    attempt_id UUID NOT NULL,
    generation_id UUID NOT NULL,
    actor_key TEXT NOT NULL CHECK (actor_key ~ '^[0-9a-f]{64}$'),
    use_case TEXT NOT NULL CHECK (use_case IN ('studio-chat', 'reporting-narrative', 'imagery-inference')),
    workflow_key TEXT CHECK (workflow_key IS NULL OR workflow_key ~ '^[0-9a-f]{64}$'),
    admitted_at TIMESTAMPTZ NOT NULL,
    deadline_at TIMESTAMPTZ NOT NULL,
    lease_expires_at TIMESTAMPTZ NOT NULL,
    released_at TIMESTAMPTZ,
    grant_epoch BIGINT NOT NULL CHECK (grant_epoch > 0),
    PRIMARY KEY (deployment_id, attempt_id),
    CHECK (admitted_at < deadline_at AND deadline_at < lease_expires_at)
);
CREATE INDEX IF NOT EXISTS idx_ai_execution_attempts_time
    ON $HonuaSchema$.ai_execution_attempts (deployment_id, admitted_at);
CREATE INDEX IF NOT EXISTS idx_ai_execution_attempts_actor_time
    ON $HonuaSchema$.ai_execution_attempts (deployment_id, actor_key, admitted_at);
CREATE TABLE IF NOT EXISTS $HonuaSchema$.ai_execution_control_events (
    deployment_id TEXT NOT NULL REFERENCES $HonuaSchema$.ai_execution_policies(deployment_id),
    invocation_id TEXT NOT NULL CHECK (length(invocation_id) BETWEEN 1 AND 128),
    request_hash TEXT NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    action TEXT NOT NULL CHECK (action IN ('Initialize', 'UpdatePolicy', 'Stop', 'Resume')),
    actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 4096),
    correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 128),
    recorded_at TIMESTAMPTZ NOT NULL,
    result_json JSONB NOT NULL,
    PRIMARY KEY (deployment_id, invocation_id)
);
COMMENT ON TABLE $HonuaSchema$.ai_execution_attempts IS 'Admitted model attempts and fixed concurrency reservations; charges are not refunded on release.';
COMMENT ON TABLE $HonuaSchema$.ai_execution_control_events IS 'Transactional control audit and exact invocation replay receipts; no model payloads or credentials.';
