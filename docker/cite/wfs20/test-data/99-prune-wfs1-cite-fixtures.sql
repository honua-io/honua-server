-- Keep the WFS 2.0 CITE stack focused on WFS 2.0 fixture layers.
-- The shared seed also contains WFS 1.0/1.1 canonical cdf/cgf fixtures; those
-- are intentionally retained for the legacy WFS stacks but are not valid WFS
-- 2.0 CITE feature type advertisements.

BEGIN;

DELETE FROM features WHERE layer_id BETWEEN 10 AND 25;
DELETE FROM honua.service_layers WHERE service_name = 'cite' AND layer_id BETWEEN 10 AND 25;
DELETE FROM honua.layer_fields WHERE layer_id BETWEEN 10 AND 25;
DELETE FROM honua.layers WHERE layer_id BETWEEN 10 AND 25;

COMMIT;
