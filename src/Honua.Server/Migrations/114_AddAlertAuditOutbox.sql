-- Alert lifecycle mutations and their domain audit record used to be two
-- independent operations: the endpoint wrote acknowledge/suppress/resolve state
-- and then called IAuditLog. A failure or a process death between them left a
-- successful, externally observable lifecycle mutation with no matching alert
-- domain audit record, and nothing existed to complete it afterwards (#3865).
--
-- This outbox closes that window. The lifecycle upsert and the audit INTENT are
-- written in ONE transaction, so a committed mutation always carries either its
-- audit record or a durable reconciliation record that deterministically
-- completes it. The reconciler drains pending intents after a restart and
-- records the audit event with the ORIGINAL actor, action, note, timestamp and
-- correlation id, so the two evidence trails agree.
CREATE TABLE IF NOT EXISTS honua.alert_audit_outbox (
    outbox_id        BIGSERIAL PRIMARY KEY,
    event_id         BIGINT      NOT NULL REFERENCES honua.alert_events(event_id) ON DELETE CASCADE,
    action           TEXT        NOT NULL,
    actor            TEXT        NOT NULL,
    note             TEXT        NULL,
    details          TEXT        NOT NULL DEFAULT '',
    correlation_id   TEXT        NOT NULL,
    idempotency_key  TEXT        NOT NULL,
    occurred_at      TIMESTAMPTZ NOT NULL,
    audit_id         TEXT        NULL,
    completed_at     TIMESTAMPTZ NULL,
    attempts         INTEGER     NOT NULL DEFAULT 0,
    last_error       TEXT        NULL,
    -- Retry backoff. A poison intent the audit sink keeps rejecting must not
    -- monopolise every oldest-first batch and starve newer, healthy intents.
    next_attempt_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Completion lease. A completer claims an intent before writing its audit
    -- record so the request path and the reconciler - or two reconcilers - cannot
    -- both write one. The lease expires so a claimant that dies does not strand
    -- the intent, and the token proves at completion time that the writer still
    -- held the lease it wrote under.
    claimed_until    TIMESTAMPTZ NULL,
    claim_token      UUID        NULL
);

COMMENT ON TABLE honua.alert_audit_outbox IS
    'Durable audit intents written in the same transaction as the alert lifecycle mutation they describe (#3865)';

-- The operator retry identity. A repeat of the same action with the same
-- idempotency identity must produce ONE logical lifecycle transition and ONE
-- domain audit action, so the key is unique and the insert is ON CONFLICT.
CREATE UNIQUE INDEX IF NOT EXISTS ux_alert_audit_outbox_idempotency
    ON honua.alert_audit_outbox (idempotency_key);

-- The reconciler's drain predicate: pending intents, oldest first.
CREATE INDEX IF NOT EXISTS ix_alert_audit_outbox_pending
    ON honua.alert_audit_outbox (next_attempt_at, outbox_id)
    WHERE completed_at IS NULL;
