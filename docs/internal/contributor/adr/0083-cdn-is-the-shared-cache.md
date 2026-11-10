# ADR-0083: The CDN is the shared cache

## Status

Proposed (2026-11)

## Context

The supported deployment is one ECS service behind a load balancer, with CloudFront in front and S3
for files. Redis is leaving the product, so there is no shared response cache inside the server any
more: the CDN is the only cache that every client shares.

Three things were missing for that to be safe and useful:

- Only some responses set `Cache-Control`, and each surface wrote its own string. A response with an
  `ETag` and no `Cache-Control` may be stored by a shared cache on heuristics.
- Objects written to object storage carried no `Cache-Control`.
- Nothing told the CDN that content had changed: not a rollout, a rollback, a publish, a change of
  access, or a data edit.

## Decision

### 1. One policy decides the header, and the default is `private, no-store`

A response is shared-cacheable only when all of the following hold. Anything else, including a
response whose class cannot be decided, is sent as `Cache-Control: private, no-store`.

| Condition | Where it is checked |
| --- | --- |
| The route declares a public response class (`WithSharedCacheClass`) | Endpoint metadata; routes without it are never shared-cacheable |
| The request is `GET` or `HEAD` and the status is 200, 204, 206 or 304 | Guard, when the response starts |
| The request carries no credential, names no tenant, and the principal is anonymous with no claims | Guard, when the response starts |
| The access pipeline granted this request as an anonymous caller, and refused nothing | Recorded where access is evaluated and where it is refused (`AccessPolicyHelpers`) |
| No authentication decision was made, no cookie is set, and the body is not a GeoServices error envelope | Guard, when the response starts |

"Secured" is therefore not looked up when the header is written. It is the absence of proof that an
anonymous caller was granted access. A layer whose access policy does not allow anonymous access
denies the anonymous caller, so no grant is recorded; a credentialed caller is refused by the request
check before the grant is considered.

One callback runs when the response starts (`AuthenticationResponseCacheMiddleware`, first in the
pipeline so that it runs last). It applies the authentication policy and then the guard. The guard
only removes cacheability: it never adds it. A handler that writes `public` by hand on a route with
no declared class is overwritten.

Why sharing the anonymous answer is sound on this deployment:

- Row-level security and field masks are selected by the caller's role claims and the layer. An
  anonymous caller has no roles and no claim values, so every anonymous caller gets the same rows
  and fields.
- A grant never admits an anonymous caller; anonymous access comes only from the resource's access
  policy, which is the same for every anonymous caller.
- The licence that filters metadata documents is one per deployment. Earlier work kept responses
  `private` because a CDN cannot see the licence. With one licence per deployment the only exposure
  is a licence change, and it lasts at most the metadata lifetime (60 seconds at the CDN).
- A request that names a tenant, by claim or by the `X-Honua-Tenant` header, is never shared.

### 2. Response classes

Lifetimes are seconds. `max-age` is for browsers, which cannot be purged; `s-maxage` is for the CDN,
which can. `s-maxage` is always written, because the CDN's own minimum is 0 and the origin header is
the only lifetime it has.

| Response class | `Cache-Control` | `ETag` | `Vary` | Lifetime comes from |
| --- | --- | --- | --- | --- |
| Public tile | `public, max-age=60, s-maxage=300, stale-while-revalidate=60, stale-if-error=86400` | Yes | `Origin, Accept, Accept-Encoding` | Per-layer setting, then the tileset setting, then `SharedCache:Tile` |
| Public feature query | `public, max-age=0, s-maxage=30, stale-while-revalidate=30, stale-if-error=86400` | Yes | `Origin, Accept, Accept-Encoding, Prefer` | Per-layer setting, then `SharedCache:FeatureQuery` |
| Capabilities and metadata documents | `public, max-age=30, s-maxage=60` | Yes | `Origin, Accept, Accept-Encoding, Accept-Language` | `SharedCache:MetadataDocument` |
| Static map image | `public, max-age=0, s-maxage=60, stale-while-revalidate=30` | Yes | `Origin, Accept-Encoding` | Per-layer setting, then `SharedCache:StaticMapImage` |
| Stored object served by the server from a key that is overwritten in place (scene assets) | `public, max-age=60, s-maxage=300` | Yes, from the store | `Origin, Accept-Encoding` | The dataset's own setting, then `SharedCache:StaticObject` |
| Secured anything (credential on the request, authenticated principal, tenant named, denied or undecided access) | `private, no-store` | Unchanged | Unchanged | Not configurable |
| Admin | `private, no-store` | Unchanged | Unchanged | Not configurable |
| Editing (any method other than `GET` or `HEAD`) | `private, no-store` | Unchanged | Unchanged | Not configurable |
| Errors (every 4xx and 5xx, and every GeoServices error envelope sent as 200) | `private, no-store` | Unchanged | Unchanged | Not configurable |

Objects written to object storage carry `public, max-age=60, s-maxage=300` when the object is public
and its key is overwritten in place, and `private, no-store` otherwise, including exports and job
outputs, which are handed out as presigned URLs and never pass through the CDN. No stored object is
both public and content-addressed today, so nothing is written as `immutable` yet.

Three controls set a public lifetime, in increasing precedence: the product default above; a
per-layer lifetime (or the per-tileset lifetime where one is configured); and the emergency setting,
which forces `max-age=0, s-maxage=15` for the named public layers, or for all of them, without
changing any stored per-layer value, and is reversed by unsetting it.

`stale-if-error` is written only together with `public`, by the one component that writes `public`,
so it never appears on a secured response.

A feature query with an arbitrary extent or a spatial predicate (`bbox`, `geometry`, a CQL2 or
OData spatial function) is never shared: every request is a near-unique address.

If a table is edited outside Honua, the server does not see the change: tiles and queries refresh
after the layer's cache lifetime, or immediately when an operator purges the layer.

Every response knows its subject: the service and layer an operator sees in
`/rest/services/{serviceId}/FeatureServer/{layerId}`, taken from the publication that granted access
on every surface (tiles, queries, scenes), or from the route when no publication was resolved. Lifetimes and the emergency setting are chosen by it today. Invalidation by
cache tag, planned for the next release candidate, derives its tags from the same subject without
touching a call site.

### 3. Addresses do not carry a generation

There is no per-layer generation to put in an address: the tile cache's "generation" is the stored
object's ETag, and the only numeric change signal is one global counter that edits through Honua's
APIs advance. Freshness is therefore governed by lifetimes and by invalidation.

### 4. Invalidation is a provider

`ICdnInvalidator` takes a list of paths and returns a result. The default does nothing and says so.
The CloudFront implementation uses the task role, the distribution id in
`SharedCache:CloudFront:DistributionId`, and batches paths. A failure is returned, never thrown and
never swallowed: release operations record it in the operation's warnings, and admin operations
report it in the response.

| Operation | Paths | Ordering |
| --- | --- | --- |
| A task starts on a release other than the one last recorded | `/*` | Once per release change: the record is one database row changed by compare-and-set |
| Rollout or rollback completes in the control plane | `/*` | After the operation is terminal; a failure cannot fail or block it |
| Layer, service or scene published, unpublished, enabled, disabled, secured or made public; style or read policy changed; operator purge | `/*` | After commit, before the response |
| Data edited through Honua's APIs | `/tiles/{layerId}/*`, `/ogc/*`, `/rest/services/{serviceId}/*` | At once for the first edit, then at most one invalidation every 30 seconds per node for all edits |

An administrative change invalidates everything, not the resource's own prefixes. Routes match
without regard to case and the CDN keys with regard to it, so a prefix cannot reach every cached
spelling of an address, and a change that reduces access must not leave one behind. These changes
are rare and lifetimes are short. An edit only needs freshness, so it names the layer's prefixes in
the spelling given and in lower case, and relies on the lifetime for any other spelling.

CloudFront allows 15 wildcard paths in progress at once across a distribution. One edit is three or
four wildcard paths; edits inside a window are sent together when it closes, and more than six
distinct paths are replaced by `/*`. When CloudFront answers that too many invalidations are in
progress, the request is retried after 2, 5 and 10 seconds and then reported as failed.

A deployment that replaces tasks does not pass through the control plane, so the release change is
detected at startup: `honua.cdn_release_record` holds the release last announced to the CDN.

### 5. Rollouts

A serving task has no cheap way to know that a rollout is in progress, so lifetimes are not
shortened during one. The first task of a new release invalidates `/*` when it starts, and the task
that flips the record back on a rollback does the same. Until the old tasks are gone a response of
either release can be cached again, for at most its lifetime.

## Consequences

- Every response carries `Cache-Control`. Routes that do not declare a class are `private, no-store`,
  which is safe and costs cache efficiency until they declare one. WMS and WMTS key-value routes are
  in that group.
- A route-table test fails if an admin route, an authorized route or a write route declares a public
  class, and sweeps every route with every credential carrier to prove the guard.
- The CloudFront cache policies need a minimum lifetime of 0, a default equal to the product default
  for that behaviour, a maximum of one year, and an error caching minimum of 0. Every credential
  carrier must reach the origin, and `Origin` and the negotiation headers must be in the cache key.
- `TileOptions:CacheMaxAge` no longer sets the tile lifetime: its default of one hour would override
  the product default on every tile. A tileset entry under `TileOptions:TilesetLifecycle` still does.
- A request that names a tenant is never shared, so tenant-scoped content is not cached by the CDN.
- An anonymous caller that edits a public layer can read a feature query up to `s-maxage` old through
  the CDN.
