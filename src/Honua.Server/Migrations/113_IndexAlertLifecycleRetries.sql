-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Bound retry lookups while retaining pre-existing audit history unchanged.
-- Atomic domain writers serialize retry admission under the audit-chain lock.
CREATE INDEX IF NOT EXISTS idx_audit_log_alert_lifecycle_retry
    ON honua.audit_log (actor, correlation_id, action, resource_id, audit_id DESC)
    WHERE resource_type = 'alert_event' AND outcome = 'Success';
