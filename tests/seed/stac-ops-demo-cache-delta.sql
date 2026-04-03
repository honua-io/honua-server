-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

BEGIN;

-- Advance the healthy collection's live item timestamps after the STAC metadata
-- endpoints have been warmed so cached collection-listing temporal extents drift
-- behind live search/item probes.
UPDATE features
SET attributes = jsonb_set(attributes, '{observed_at}', '"2026-04-12T19:00:00Z"'::jsonb)
WHERE layer_id = 68810
  AND objectid = 6881001;

UPDATE features
SET attributes = jsonb_set(attributes, '{observed_at}', '"2026-04-13T19:00:00Z"'::jsonb)
WHERE layer_id = 68810
  AND objectid = 6881002;

UPDATE features
SET attributes = jsonb_set(attributes, '{observed_at}', '"2026-04-14T19:00:00Z"'::jsonb)
WHERE layer_id = 68810
  AND objectid = 6881003;

UPDATE features
SET attributes = jsonb_set(attributes, '{observed_at}', '"2026-04-15T19:00:00Z"'::jsonb)
WHERE layer_id = 68810
  AND objectid = 6881004;

COMMIT;
