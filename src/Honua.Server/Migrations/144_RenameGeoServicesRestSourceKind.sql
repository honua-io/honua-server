-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Stored migration runs, batches and catalog rows name the GeoServices REST source kind by its current
-- value. Fingerprinted evidence (migration_runs.evidence_pack_body, migration_performance_evidence) keeps
-- its recorded bytes; readers accept both values.

UPDATE honua.migration_runs
SET source_kind = 'geoservices-rest'
WHERE source_kind = 'arcgis-geoservices-rest';

UPDATE honua.migration_batch_runs
SET source_kind = 'geoservices-rest'
WHERE source_kind = 'arcgis-geoservices-rest';

UPDATE honua.migration_data_sources AS legacy
SET source_kind = 'geoservices-rest'
WHERE legacy.source_kind = 'arcgis-geoservices-rest'
  AND NOT EXISTS (
      SELECT 1 FROM honua.migration_data_sources AS current_row
      WHERE current_row.source_kind = 'geoservices-rest' AND current_row.source_id = legacy.source_id);

UPDATE honua.migration_styles AS legacy
SET source_kind = 'geoservices-rest'
WHERE legacy.source_kind = 'arcgis-geoservices-rest'
  AND NOT EXISTS (
      SELECT 1 FROM honua.migration_styles AS current_row
      WHERE current_row.source_kind = 'geoservices-rest' AND current_row.source_id = legacy.source_id);

UPDATE honua.arcgis_migration_runs
SET manifest_json = replace(manifest_json::text, '"arcgis-geoservices-rest"', '"geoservices-rest"')::jsonb
WHERE manifest_json::text LIKE '%"arcgis-geoservices-rest"%';
