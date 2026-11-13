# ADR-0086: One source of truth and one generation number for configuration and caches

## Status
Accepted (2026-11-12). Founder-approved. Builds on [ADR-0082](0082-postgres-control-plane-state.md)
(PostgreSQL holds all shared state) and [ADR-0083](0083-cdn-is-the-shared-cache.md) (the CDN is the
shared cache). First stage ships in 2026.1-rc.2 with honua-server#7392.

## Context
The certified deployment runs two to ten ECS tasks behind CloudFront on one PostgreSQL database,
with no Redis and no message bus. A setting changed through one task reaches the others only if it
is read from PostgreSQL. Three kinds of state did not follow that rule:

- Some admin settings (OIDC providers, client-certificate trust and revocations, rate-limit
  policies) have no PostgreSQL store at all. A change reaches the task that received it and is lost
  on restart.
- The Pro output cache keyed entries by the catalog revision only on GeoServices routes. OGC API,
  tile, style and STAC entries on other tasks lived out their lifetime, up to an hour, after a
  catalog or access change, and an in-process lifetime longer than the CDN lifetime let a task
  refill the CDN with an old response after a purge.
- The catalog snapshot was re-read and re-parsed on a timer although a revision probe already said
  whether anything had changed. Nothing reported which cache served a response or which change
  caused a purge.

Each was fixed, or left unfixed, by its own mechanism: Redis-era refresh broadcasts, per-feature
invalidators, timed reloads. That is what let them disagree.

## Decision
1. **PostgreSQL is the only source of truth for mutable configuration.** The catalog, security
   settings (OIDC providers, client-certificate trust and revocations, rate limits) and cache policy
   live in PostgreSQL. Nothing an operator can configure exists only in a task's memory. Until a
   setting has its PostgreSQL store, a deployment configured for more than one task refuses to
   change it, with an error that says why.
2. **One generation number.** A single monotonically increasing generation is bumped in the same
   transaction as any configuration change. Tasks read it: at most every 2 s for reads, and
   immediately before writes, admin requests and tool calls. There is no pub/sub. `LISTEN/NOTIFY`
   may later wake the poll early as a latency optimisation layered on top; it never replaces it.
3. **Every cache is keyed by the generation.** The catalog snapshot, the output cache and ETags carry
   it, so an entry from an older generation becomes unreachable without invalidation code. A cached
   response about a protected resource is replayed only if the access check granted it under the
   generation in its key.
4. **The in-process cache never outlives the CDN.** An in-process lifetime is at most the CDN
   `s-maxage` for the same route class. CloudFront uses short `s-maxage` and ETag revalidation with
   the generation in the ETag. Purges are coalesced, sent by one task per generation change, scoped
   by tag or path, and never `/*` by default. Credentialed responses are never shared-cacheable;
   `SharedCacheResponse` stays the single writer of a shared `Cache-Control`.
5. **Caches are observable.** Responses carry an RFC 9211 `Cache-Status` entry and the generation;
   each task exports the generation it serves as a metric; every purge is recorded with the id of
   the change that caused it.

## Consequences
- **Retired:** the Redis-era `DistributedCacheRefreshCoordinator`, per-feature cache invalidators
  where the generation key makes them redundant, timed full reloads of the catalog, and in-memory-only
  configuration stores.
- **Review rule:** code that adds an in-memory-only configuration store, or a cache whose key does not
  include the generation, cites this ADR with the reason, or the review rejects it
  ([architecture criteria](../architecture-criteria.md)).
- **Staged plan:**
  - 2026.1-rc.2, honua-server#7392: the catalog revision is the generation for the catalog. Every
    output-cache entry is keyed by it and stored only after an anonymous access grant; in-process
    lifetimes are capped at the CDN lifetime; the snapshot is reloaded only when the revision changes;
    the three in-memory settings refuse changes on multi-task deployments; `Cache-Status` and purge
    records ship; connection pools are sized from the task count and database class.
  - 2026.1-rc.3 and 2027.1, honua-server#7393 and #7394: security settings move into PostgreSQL under
    the same generation, the generation reaches ETags and the per-task metric, and purges become
    coalesced and scoped through a lease row.
- Data edits are not configuration changes and do not bump the generation; until a per-layer data
  version is part of the key, edits are bounded by the capped lifetime and the edit purge.
