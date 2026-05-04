-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.
--
-- Apply after a mobile client has downloaded the baseline offline package.
-- Advances the deterministic conflict target so a stale offline edit based on
-- sync_version = 1 can be detected against the server's sync_version = 2.

BEGIN;

UPDATE features
SET attributes = attributes
    || jsonb_build_object(
        'status', 'blocked',
        'priority', 'critical',
        'assigned_to', 'server-dispatch',
        'inspection_date', '2026-05-02T02:15:00Z',
        'sync_version', 2,
        'notes', 'Server-side dispatch update applied after offline package download.'
    ),
    updated_at = '2026-05-02T02:15:00Z'::timestamptz
WHERE layer_id = 68910
  AND objectid = 6891002
  AND attributes ->> 'offline_action' = 'conflict-target';

COMMIT;
