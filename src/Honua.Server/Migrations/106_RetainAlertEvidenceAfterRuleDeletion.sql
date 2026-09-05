-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 106_RetainAlertEvidenceAfterRuleDeletion.sql
-- Description: Rule configuration deletion must not erase immutable incident, lifecycle,
--              delivery, or acknowledgement evidence (#3866). Retention/purge jobs remain
--              responsible for eventual evidence removal.
-- Dependencies: 013_AddSpatialAlerts.sql, 075_AddAlertOpsSource.sql.

ALTER TABLE honua.alert_events
    DROP CONSTRAINT IF EXISTS alert_events_rule_id_fkey;

ALTER TABLE honua.alert_events
    ADD CONSTRAINT alert_events_rule_id_fkey
    FOREIGN KEY (rule_id)
    REFERENCES honua.alert_rules(rule_id)
    ON DELETE SET NULL;

COMMENT ON CONSTRAINT alert_events_rule_id_fkey ON honua.alert_events IS
    'Preserves immutable incident and delivery evidence when its mutable rule configuration is deleted (#3866).';
