-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 034_CreateOperateObservability.sql
-- Description: Adds operator-facing observability state for the Console Operate
--              event viewer (#1168): alert event lifecycle, investigation records,
--              and a composite audit-log index used by paginated forensic reads.
-- Dependencies: honua.alert_events (013_AddSpatialAlerts.sql) and
--               honua.audit_log (033_CreateAuditLog.sql).

-- Operator lifecycle state attached to immutable alert events. Kept in a
-- companion table so acknowledge / suppress / resolve mutations never rewrite
-- alert_events history.
-- lifecycle_status: 0=open, 1=acknowledged, 2=suppressed, 3=resolved
CREATE TABLE IF NOT EXISTS honua.alert_event_lifecycle (
    event_id           BIGINT      PRIMARY KEY REFERENCES honua.alert_events(event_id) ON DELETE CASCADE,
    lifecycle_status   SMALLINT    NOT NULL DEFAULT 0,
    acknowledged_at    TIMESTAMPTZ NULL,
    acknowledged_by    TEXT        NULL,
    suppressed_until   TIMESTAMPTZ NULL,
    suppressed_by      TEXT        NULL,
    resolved_at        TIMESTAMPTZ NULL,
    resolved_by        TEXT        NULL,
    note               TEXT        NULL,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT alert_event_lifecycle_valid_status CHECK (lifecycle_status IN (0, 1, 2, 3))
);

CREATE INDEX IF NOT EXISTS idx_alert_event_lifecycle_status_updated
    ON honua.alert_event_lifecycle (lifecycle_status, updated_at DESC);

COMMENT ON TABLE honua.alert_event_lifecycle IS
    'Operator lifecycle state (open/acknowledged/suppressed/resolved) attached to immutable alert events (#1168).';
COMMENT ON COLUMN honua.alert_event_lifecycle.lifecycle_status IS
    '0=open, 1=acknowledged, 2=suppressed, 3=resolved';

-- Composite index used by the alert-event query cursor: paginate newest-first
-- with tie-break on event_id descending.
CREATE INDEX IF NOT EXISTS idx_alert_events_occurred_event
    ON honua.alert_events (occurred_at DESC, event_id DESC);

-- Audit log: pagination cursor uses (timestamp DESC, audit_id DESC). The
-- existing single-column timestamp index produced unstable ordering for ties;
-- this composite supports stable cursor pagination from Console.
CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp_id
    ON honua.audit_log (timestamp DESC, audit_id DESC);

-- Investigation scratchpads. Append-only pin/link rows reference the parent
-- investigation row via FK so cascade delete cleans up correctly.
-- status: 0=open, 1=closed
CREATE TABLE IF NOT EXISTS honua.investigations (
    investigation_id   TEXT        PRIMARY KEY,
    title              TEXT        NOT NULL,
    status             SMALLINT    NOT NULL DEFAULT 0,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by         TEXT        NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    summary            TEXT        NULL,
    CONSTRAINT investigations_valid_title CHECK (length(title) > 0),
    CONSTRAINT investigations_valid_status CHECK (status IN (0, 1))
);

CREATE INDEX IF NOT EXISTS idx_investigations_updated_id
    ON honua.investigations (updated_at DESC, investigation_id DESC);

CREATE INDEX IF NOT EXISTS idx_investigations_status_updated
    ON honua.investigations (status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_investigations_created_by
    ON honua.investigations (created_by);

COMMENT ON TABLE honua.investigations IS
    'Operator-driven investigation scratchpads — pins events and links upstream resources for Console Operate (#1168).';
COMMENT ON COLUMN honua.investigations.status IS
    '0=open, 1=closed';

-- Pinned events on an investigation. event_kind mirrors the OperateEventKind
-- enum so Console can render pins without re-querying the underlying source.
-- event_kind: 1=Alert, 2=Audit, 3=Job, 4=Release, 5=SyncConflict, 6=TemporalOp, 7=Log
CREATE TABLE IF NOT EXISTS honua.investigation_pins (
    pin_id           BIGSERIAL   PRIMARY KEY,
    investigation_id TEXT        NOT NULL REFERENCES honua.investigations(investigation_id) ON DELETE CASCADE,
    event_ref        TEXT        NOT NULL,
    event_kind       SMALLINT    NOT NULL,
    occurred_at      TIMESTAMPTZ NOT NULL,
    note             TEXT        NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by       TEXT        NOT NULL,
    CONSTRAINT investigation_pins_valid_kind CHECK (event_kind BETWEEN 1 AND 7),
    CONSTRAINT investigation_pins_valid_ref CHECK (length(event_ref) > 0)
);

CREATE INDEX IF NOT EXISTS idx_investigation_pins_investigation
    ON honua.investigation_pins (investigation_id, pin_id);

COMMENT ON TABLE honua.investigation_pins IS
    'Events pinned to investigations. event_ref is a source-prefixed identifier (e.g., "alert:42").';

-- Linked external resources (alert, job, release, change-set).
-- resource_kind: 1=Alert, 2=Job, 3=Release, 4=ChangeSet
CREATE TABLE IF NOT EXISTS honua.investigation_links (
    link_id          BIGSERIAL   PRIMARY KEY,
    investigation_id TEXT        NOT NULL REFERENCES honua.investigations(investigation_id) ON DELETE CASCADE,
    resource_kind    SMALLINT    NOT NULL,
    resource_id      TEXT        NOT NULL,
    note             TEXT        NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by       TEXT        NOT NULL,
    CONSTRAINT investigation_links_valid_kind CHECK (resource_kind BETWEEN 1 AND 4),
    CONSTRAINT investigation_links_valid_resource CHECK (length(resource_id) > 0)
);

CREATE INDEX IF NOT EXISTS idx_investigation_links_investigation
    ON honua.investigation_links (investigation_id, link_id);

COMMENT ON TABLE honua.investigation_links IS
    'Upstream resources linked to investigations (alerts, jobs, releases, change-sets).';
