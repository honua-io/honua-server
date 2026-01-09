# ADR-0021: Redis Usage and HybridCache Deferral

## Status
Accepted

## Context

Honua Server uses Redis for multiple responsibilities:
- Metadata cache for layer/service catalogs with in-memory fallback (`RedisCacheService`).
- Output cache storage for HTTP responses (`AddStackExchangeRedisOutputCache`).
- Distributed import coordination (job queue + leader election).
- Import request/progress storage using `IDistributedCache`.

HybridCache was considered to reduce custom caching code and improve provider flexibility. However, the current Redis usage relies on features HybridCache does not provide:
- **Pattern invalidation** for catalog/relationship cache keys.
- **Distributed locks** for cache stampede protection.
- **Redis list operations** for job queue semantics.
- **Explicit leader election** using Redis locks.
- **AOT-safe serialization** already wired for cache payloads.

These behaviors are required for multi-node deployments and import processing coordination.

## Decision

Keep the current Redis-based implementations and defer HybridCache adoption.

HybridCache may be revisited if:
- It adds pattern invalidation or key-space management hooks.
- It provides cross-node stampede protection or lock integration.
- The import job queue/leader election requirements are refactored away from Redis primitives.

## Consequences

### Positive
- Preserves multi-node behavior for caching and import coordination.
- Keeps existing invalidation semantics and distributed locking.
- Avoids gaps in functionality that HybridCache would introduce today.
- Allows incremental improvements with Redis-backed integration tests.

### Negative
- More custom code to maintain (fallback logic, locks, scans).
- Less provider flexibility for cache storage (Redis-centric features).
- Requires ongoing integration testing with Redis containers.

### Follow-up
- Maintain Redis integration tests for caching and import coordination.
- Re-evaluate HybridCache when feature parity improves.
