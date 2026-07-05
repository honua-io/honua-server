-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 073_AddAlertOpsSource.sql
-- Description: Promotes the alert delivery outbox to a shared, consumer-agnostic
--              notification pipeline (alerts GA, #2427). Adds an event `source`
--              discriminator and relaxes `rule_id` to allow operations notifications
--              (deploy/job terminal events) that reuse the same outbox but are not
--              linked to an alert rule.

-- Operations-notification events have no owning rule. Allow rule_id to be NULL; the
-- foreign key still constrains rule-driven (GIS) events, and NULL is exempt from the
-- FK, so rule-scoped joins naturally exclude ops events without extra predicates.
ALTER TABLE honua.alert_events
    ALTER COLUMN rule_id DROP NOT NULL;

-- Event source discriminator: NULL / 'gis' => rule-driven geofence/dwell/threshold
-- event; 'ops' => operations notification. For 'ops' rows, rule_id/zone_id/objectid/
-- layer_id/trigger_type are inert placeholders; the meaningful payload lives in
-- service_id (the ops source), severity, and payload.
ALTER TABLE honua.alert_events
    ADD COLUMN IF NOT EXISTS source TEXT;

COMMENT ON COLUMN honua.alert_events.source IS 'Event source: NULL/gis=rule-driven alert, ops=operations notification (deploy/job terminal event) with NULL rule_id';

-- Partial index so ops-notification queries and backlog inspection do not scan the
-- rule-driven event mass.
CREATE INDEX IF NOT EXISTS ix_alert_events_source_ops
    ON honua.alert_events(occurred_at DESC)
    WHERE source = 'ops';
