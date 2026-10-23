CREATE EXTENSION IF NOT EXISTS postgis;
DROP TABLE IF EXISTS features;
CREATE TABLE features (
    objectid BIGSERIAL PRIMARY KEY,
    layer_id INTEGER NOT NULL,
    geometry geometry(Geometry, 4326),
    attributes JSONB DEFAULT '{}'::jsonb
);
INSERT INTO features (layer_id, geometry, attributes)
SELECT
    0,
    ST_SetSRID(ST_MakePoint(
        -122.50 + (-122.30 - -122.50) * ((gs % 100)::double precision / 100.0),
        37.70 + (37.82 - 37.70) * (((gs / 100) % 100)::double precision / 100.0)), 4326),
    jsonb_build_object(
        'name', 'soak feature ' || gs,
        'description', 'capacity envelope test feature ' || gs,
        'category', (ARRAY['test','sample','control','reference'])[1 + (gs % 4)],
        'timestamp', to_char(TIMESTAMPTZ '2026-01-01 00:00:00Z' + (gs || ' seconds')::interval, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
        'event_date', to_char(TIMESTAMPTZ '2026-01-01 00:00:00Z' + (gs || ' minutes')::interval, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
        'created_date', to_char(DATE '2026-01-01' + (gs % 365), 'YYYY-MM-DD'))
FROM generate_series(1, 10000) AS gs;
CREATE INDEX idx_features_geometry ON features USING GIST(geometry);
CREATE INDEX idx_features_layer_id ON features(layer_id);
ANALYZE features;
