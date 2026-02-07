# Spatial Query Troubleshooting

Use this guide when spatial queries return empty results, incorrect results, or time out.

**Scope**: CRS mismatches, invalid geometry, missing indexes, and oversize queries.

---

## Quick Diagnostics

**FeatureServer bbox query**:
```bash
curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json"
```

**OGC API Features bbox query**:
```bash
curl "http://localhost:8080/ogc/features/collections/parcels/items?bbox=-122.5,37.7,-122.3,37.8&limit=10"
```

**PostGIS version**:
```sql
SELECT PostGIS_Version();
```

---

## CRS Mismatch

**Symptom**: Queries return no features even though data exists.

**Checks**:
```sql
SELECT ST_SRID(geometry) AS srid, COUNT(*)
FROM honua.features
WHERE geometry IS NOT NULL
GROUP BY ST_SRID(geometry);
```

**Fixes**:
- Ensure your query geometry uses the same SRID as stored data.
- For FeatureServer, set `inSR` and `outSR` explicitly when needed.

---

## Invalid Geometry

**Symptom**: Errors on spatial filters or inconsistent results.

**Checks**:
```sql
SELECT feature_id, ST_IsValid(geometry), ST_IsValidReason(geometry)
FROM honua.features
WHERE NOT ST_IsValid(geometry)
LIMIT 10;
```

**Fixes**:
```sql
UPDATE honua.features
SET geometry = ST_MakeValid(geometry)
WHERE NOT ST_IsValid(geometry);
```

---

## Missing Spatial Index

**Symptom**: Spatial queries are slow even on small bbox filters.

**Check**:
```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM honua.features
WHERE ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326));
```

**Fix**:
```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_features_geometry
ON honua.features USING gist(geometry);
```

---

## Oversized or Expensive Queries

**Symptoms**: Timeouts, large payloads, excessive CPU.

**Fixes**:
- Reduce bbox size and increase paging.
- Use server-side filters to limit results.
- Consider adjusting limits: `Limits__Query__MaxRecordCount`, `Limits__Query__MaxBboxAreaSqKm`, `Limits__Query__QueryTimeout`, `Limits__Geometry__MaxVerticesPerGeometry`, `Limits__Geometry__SimplifyTolerance`.

---

## Related Docs

- [Query Optimization](../query-optimization.md)
- [Performance Monitoring](../performance-monitoring.md)
