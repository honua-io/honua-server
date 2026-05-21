-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable audit log for security-relevant events (#1144).
-- Append-only sink — no UPDATE / DELETE policy is enforced at the table
-- level via a rule that blocks both operations, so forensic / compliance
-- consumers (and the ELv2 enterprise pitch) have a tamper-resistant store.
-- The application layer (PostgresAuditLog) only ever issues INSERTs.

CREATE TABLE IF NOT EXISTS honua.audit_log (
    audit_id         BIGSERIAL    PRIMARY KEY,
    timestamp        TIMESTAMPTZ  NOT NULL,
    event_type       VARCHAR(32)  NOT NULL,
    actor            VARCHAR(256) NOT NULL,
    actor_type       VARCHAR(32)  NOT NULL,
    resource_type    VARCHAR(64)  NOT NULL,
    resource_id      VARCHAR(256),
    action           VARCHAR(128) NOT NULL,
    outcome          VARCHAR(16)  NOT NULL,
    correlation_id   VARCHAR(64)  NOT NULL,
    remote_ip        VARCHAR(64),
    user_agent       VARCHAR(512),
    details          TEXT         NOT NULL DEFAULT '',

    CONSTRAINT chk_audit_log_event_type CHECK (event_type IN (
        'Authentication','Authorization','AdminAction',
        'ConfigChange','DataExport','DataDelete')),
    CONSTRAINT chk_audit_log_actor_type CHECK (actor_type IN (
        'Anonymous','UserId','ApiKey','System')),
    CONSTRAINT chk_audit_log_outcome CHECK (outcome IN (
        'Success','Failure','Denied'))
);

-- Forensic queries are always time-bounded; this is the dominant access path.
CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp
    ON honua.audit_log (timestamp DESC);

-- "Show me everything actor X did, newest first."
CREATE INDEX IF NOT EXISTS idx_audit_log_actor_timestamp
    ON honua.audit_log (actor, timestamp DESC);

-- "Show me all auth failures in the last 24h" / "all admin actions in the window."
CREATE INDEX IF NOT EXISTS idx_audit_log_event_type_timestamp
    ON honua.audit_log (event_type, timestamp DESC);

-- Append-only enforcement: block UPDATE and DELETE at the rule layer so
-- accidental application bugs (or compromised service accounts that lack
-- DROP RULE privilege) cannot rewrite history. Privileged DBA workflows can
-- still rotate / archive via partition swap or explicit DROP RULE in a
-- maintenance window — this is intentional.
DROP RULE IF EXISTS audit_log_no_update ON honua.audit_log;
CREATE RULE audit_log_no_update AS ON UPDATE TO honua.audit_log DO INSTEAD NOTHING;

DROP RULE IF EXISTS audit_log_no_delete ON honua.audit_log;
CREATE RULE audit_log_no_delete AS ON DELETE TO honua.audit_log DO INSTEAD NOTHING;

COMMENT ON TABLE  honua.audit_log IS
    'Append-only audit trail for security-relevant events (#1144). UPDATE / DELETE are blocked by rule.';
COMMENT ON COLUMN honua.audit_log.actor IS
    'Stable identifier of the principal: user id, hashed api-key id, "anonymous", or service name.';
COMMENT ON COLUMN honua.audit_log.details IS
    'Pre-sanitized structured detail (JSON-as-text by convention). Stored opaquely — caller is responsible for redaction.';
