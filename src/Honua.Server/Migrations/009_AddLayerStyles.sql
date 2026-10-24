-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Add layer style storage for MapLibre and GeoServices drawingInfo.

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS maplibre_style JSONB;

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS geoservices_drawing_info JSONB;

ALTER TABLE honua.layers
    ADD COLUMN IF NOT EXISTS style_version INT DEFAULT 1;

COMMENT ON COLUMN honua.layers.maplibre_style IS 'Canonical MapLibre style JSON for the layer';
COMMENT ON COLUMN honua.layers.geoservices_drawing_info IS 'Cached GeoServices drawingInfo JSON for FeatureServer';
COMMENT ON COLUMN honua.layers.style_version IS 'Style version incremented on MapLibre updates';
