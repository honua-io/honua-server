# Caching Quick Reference

Use this page for a fast reminder of what to cache and where to measure it.

| Surface | Typical Cache | Notes |
|---------|---------------|------|
| OGC / FeatureServer metadata | Output cache | Short TTLs |
| Tiles | Edge cache + Cache-Control | High hit rates |
| Query responses | Response cache | Only when safe for anonymous GETs |
| Prepared statements | Database cache | Check query cache stats |

## Metrics

- `GET /api/v1/metrics/cache`
- `GET /api/v1/admin/performance/database/query-cache/statistics`

## Related Docs

- [Caching Strategy](CACHING_STRATEGY.md)
- [Database Query Caching](DATABASE_QUERY_CACHING.md)
