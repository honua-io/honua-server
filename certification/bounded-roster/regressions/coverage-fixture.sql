-- Apply only to the isolated client-compat database, after its normal seed.
-- Four rows (north to south) and four columns (west to east). value=10*row+col,
-- except row 1, col 2 is nodata. The central 2x2 trim is [11,-9999;21,22].
BEGIN;
INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
VALUES ('coverage_4996', 'Coverage client regression', 4326, ARRAY['JSON'], ARRAY['Query'],
        ST_MakeEnvelope(-124,36,-120,40,4326), '{"accessPolicy":{"allowAnonymous":true}}');
INSERT INTO honua.layers (layer_id, layer_name, description, table_name, geometry_type, srid, extent, metadata)
VALUES (2496, 'Coverage 4996', 'Independent four by four gradient', 'features', 'Polygon', 4326,
        ST_MakeEnvelope(-124,36,-120,40,4326), '{"accessPolicy":{"allowAnonymous":true}}');
INSERT INTO honua.service_layers (service_name, layer_id, layer_order) VALUES ('coverage_4996',2496,0);
INSERT INTO honua.raster_data (layer_id, name, description, raster)
VALUES (2496, 'gradient-with-nodata', 'value=10*row+col, nodata at row=1 col=2',
        ST_MapAlgebra(
            ST_AddBand(ST_MakeEmptyRaster(4,4,-124,40,1,-1,0,0,4326), '32BF'::text, 0, -9999),
            1, '32BF', 'CASE WHEN [rast.x]=3 AND [rast.y]=2 THEN -9999 ELSE 10*([rast.y]-1)+[rast.x]-1 END'));
SELECT honua.seed_metadata_v2_compat_snapshot();
COMMIT;
