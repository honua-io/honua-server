\timing on
-- z0/0/0, target SRID 3857, extent 4096, buffer 256 (server defaults)
SELECT octet_length(ST_AsMVT(tile, 'layer', 4096, 'geom')) AS z0_bytes
FROM (
    SELECT
        objectid::bigint AS objectid,
        attributes::jsonb AS attributes,
        ST_AsMVTGeom(ST_Transform(geometry::geometry, 3857), ST_TileEnvelope(0,0,0), 4096, 256) AS geom
    FROM features
    WHERE layer_id = 0
      AND geometry::geometry && ST_Transform(ST_Intersection(ST_Expand(ST_TileEnvelope(0,0,0), 2504688.5), ST_TileEnvelope(0,0,0)), 4326)
      AND ST_Intersects(geometry::geometry, ST_Transform(ST_Intersection(ST_Expand(ST_TileEnvelope(0,0,0), 2504688.5), ST_TileEnvelope(0,0,0)), 4326))
    LIMIT 50000
) AS tile;
