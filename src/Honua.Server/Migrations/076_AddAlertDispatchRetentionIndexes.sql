-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 076_AddAlertDispatchRetentionIndexes.sql
-- Description: Keeps the alert delivery outbox backlog count cheap and the outbox
--              bounded (alerts GA follow-up, #2481). Before this, GetBacklogAsync
--              scanned the whole honua.alert_dispatch table (the only partial index
--              covered status IN (0,3)) every dispatch pass, and delivered (status=2)
--              rows were never purged, so both grew without bound.

-- Serve the backlog count (status IN (0,1,3) and status = 4) from a partial index that
-- excludes delivered rows. Paired with the `WHERE status <> 2` predicate in
-- GetBacklogAsync so the planner skips the delivered mass entirely.
CREATE INDEX IF NOT EXISTS ix_alert_dispatch_active
    ON honua.alert_dispatch(status)
    WHERE status <> 2;

-- Serve the retention delete of delivered rows (status = 2 AND delivered_at < cutoff)
-- so the periodic purge does not seq-scan the delivered mass.
CREATE INDEX IF NOT EXISTS ix_alert_dispatch_delivered
    ON honua.alert_dispatch(delivered_at)
    WHERE status = 2;
