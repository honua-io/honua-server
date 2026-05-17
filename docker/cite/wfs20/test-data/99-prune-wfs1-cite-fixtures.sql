-- Keep the WFS 2.0 CITE stack focused on applicable WFS 2.0 fixture layers.
-- The shared seed also contains WFS 1.0/1.1 canonical cdf/cgf fixtures; those
-- are intentionally retained for the legacy WFS stacks but are not valid WFS
-- 2.0 CITE feature type advertisements.
--
-- The official WFS 2.0 ETS marks curve/polygon Intersects tests as skipped for
-- point-valued feature types. The strict no-skip evidence profile therefore uses
-- the line and polygon feature types below, which exercise the applicable
-- spatial filter variants without generating point-only N/A skips.

BEGIN;

UPDATE features
SET attributes = jsonb_set(attributes, '{status}', 'null'::jsonb, true)
WHERE layer_id = 2
  AND objectid = 102;

DELETE FROM features WHERE layer_id = 1;
DELETE FROM honua.service_layers WHERE service_name = 'cite' AND layer_id = 1;
DELETE FROM honua.layer_fields WHERE layer_id = 1;
DELETE FROM honua.layers WHERE layer_id = 1;

DELETE FROM features WHERE layer_id BETWEEN 10 AND 25;
DELETE FROM honua.service_layers WHERE service_name = 'cite' AND layer_id BETWEEN 10 AND 25;
DELETE FROM honua.layer_fields WHERE layer_id BETWEEN 10 AND 25;
DELETE FROM honua.layers WHERE layer_id BETWEEN 10 AND 25;

COMMIT;
