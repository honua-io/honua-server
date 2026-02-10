# Caching Strategy

Honua uses a layered caching approach to improve read performance and reduce database load.

**Scope**: What is cached, where it lives, and how to observe it.

---

## Layers

1. **Edge/CDN caching**
   - Recommended for public read endpoints and tile traffic.

2. **Server output caching**
   - Metadata endpoints are cached with short TTLs.
   - Cache invalidation happens on writes where applicable.

3. **Response caching for queries**
   - Query responses can be cached when safe (typically anonymous GETs).

4. **Database query caching**
   - Prepared statement caching and query stats are exposed via metrics.

---

## Observability

- `GET /api/v1/metrics/cache`
- `GET /api/v1/admin/performance/database/query-cache/statistics`

---

## Guidance

- Cache only safe, anonymous GETs.
- Use conservative TTLs for metadata and tiles.
- Always invalidate on write operations.

---

## Related Docs

- [Caching Quick Reference](CACHING_QUICK_REFERENCE.md)
- [Database Query Caching](DATABASE_QUERY_CACHING.md)
- [Performance Monitoring](performance-monitoring.md)
