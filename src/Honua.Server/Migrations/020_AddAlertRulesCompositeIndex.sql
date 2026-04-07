-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 020: Add composite index for alert_rules dwell sweep performance
--
-- PERFORMANCE OPTIMIZATION: This index optimizes the dwell sweep query in PostgresAlertStateStore
-- that performs INNER JOIN between alert_state and alert_rules with filters on:
-- - r.is_active = TRUE
-- - r.trigger_type = @dwell_trigger_type
--
-- Without this composite index, the query performs O(n) scans instead of O(log n) lookups
-- under production load with many alert rules.

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_alert_rules_dwell_sweep
    ON honua.alert_rules(rule_id, is_active, trigger_type)
    WHERE is_active = TRUE;

-- Add index usage comment for future reference
COMMENT ON INDEX honua.idx_alert_rules_dwell_sweep IS
    'Composite index for dwell sweep performance - optimizes JOIN + filter operations in PostgresAlertStateStore.GetDwellCandidatesAsync()';