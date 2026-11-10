# ADR-0081: The CDN is the shared cache

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
| The request is `GET` or `HEAD` and the status is 200, 206 or 304 | Guard, when the response starts |
| The request carries no credential (any header, query parameter, cookie or client certificate the server accepts) and the principal is anonymous with no claims | Guard, when the response starts |
| The access pipeline granted this request as an anonymous caller, and denied nothing | Recorded where access is decided (`AccessPolicyHelpers.CreateAccessDeniedResult`) |
| No authentication decision was made and no cookie is set | Guard, when the response starts |
| No rollout is in progress, unless the address is generation-addressed | Guard, when the response starts |

"Secured" is therefore not looked up when the header is written. It is the absence of proof that an
anonymous caller was granted access. A layer whose access policy does not allow anonymous access
denies the anonymous caller, so no grant is recorded; a credentialed caller is refused by the request
check before the grant is considered.

The guard runs last, when the response starts. It only removes cacheability: it never adds it. A
handler that writes `public` by hand on a route with no declared class is overwritten.

### 2. Response classes

Lifetimes are seconds. `max-age` is for browsers, which cannot be purged; `s-maxage` is for the CDN,
which can.

| Response class | `Cache-Control` | `ETag` | `Vary` | Lifetime comes from |
| --- | --- | --- | --- | --- |
| Public tile, generation-addressed (`g` matches the current generation) | `public, max-age=31536000, s-maxage=31536000, immutable` | Yes | `Accept-Encoding` | Fixed: the address changes instead |
| Public tile, plain address | `public, max-age=3600, s-maxage=3600` | Yes | `Accept-Encoding` | Per-layer setting, then the tileset setting, then `SharedCache:Tile` |
| Public feature query | `public, max-age=0, s-maxage=60` | Yes | `Accept, Accept-Encoding, Prefer` | Per-layer setting, then `SharedCache:FeatureQuery` |
| Capabilities and metadata documents | `public, max-age=30, s-maxage=60` | Yes | `Accept, Accept-Encoding, Accept-Language` | `SharedCache:MetadataDocument` |
| Static map image | `public, max-age=300, s-maxage=300` | Yes | `Accept-Encoding` | Per-layer setting, then `SharedCache:StaticMapImage` |
| Static stored object served by the server (scene assets, raster and tile archives) | `public, max-age=3600, s-maxage=3600` | Yes | `Accept-Encoding` | The dataset's own setting, then `SharedCache:StaticObject` |
| Secured anything (credential on the request, authenticated principal, denied or undecided access) | `private, no-store` | Unchanged | Unchanged | Not configurable |
| Admin | `private, no-store` | Unchanged | Unchanged | Not configurable |
| Editing (any method other than `GET` or `HEAD`) | `private, no-store` | Unchanged | Unchanged | Not configurable |

`SharedCache:EmergencyLifetimeSeconds` caps every public lifetime, and removes `immutable`, in one
step.

### 3. A generation in long-lived addresses

The generation is a short hash of three values the server already has: the catalog revision (layer
and style changes), the feature change generation (data edits), and the release revision. No new
counter is added.

It travels as the query parameter `g`, which must be part of the CDN cache key. A tile request whose
`g` equals the current generation gets the immutable lifetime. Any other tile request, with no `g`
or a stale one, gets the plain-address lifetime, so a client that builds tile addresses by
convention keeps working.

Clients obtain the current address from the documents that already hand out tile addresses: OGC
tileset links, TileJSON, style documents, VectorTileServer resources, WMTS capabilities and terrain
metadata. Those documents are in the metadata class, so a new generation reaches clients within its
lifetime.

### 4. Invalidation is a provider

`ICdnInvalidator` takes a list of paths and returns a result. The default does nothing and says so.
The CloudFront implementation uses the task role, one distribution id from configuration, and
batches paths. A failure is returned, never thrown and never swallowed: release operations record it
in the operation's warnings, and admin operations report it in the response.

| Operation | Paths | Ordering |
| --- | --- | --- |
| Rollout completes | `/*` | After the operation is terminal; a failure cannot fail it |
| Rollback completes | `/*` | After the operation is terminal; a failure cannot block it |
| Layer or service published or enabled | That resource's path prefixes | After commit |
| Layer or service unpublished, disabled or secured | That resource's path prefixes | After commit, before the response |
| Service made public | That resource's path prefixes | After commit |
| Style changed | That layer's path prefixes | After commit |
| Operator purge, one layer | That layer's path prefixes | On request |
| Operator purge, everything | `/*` | On request |

Data edits do not invalidate: the generation changes the address.

### 5. Rollouts

While a deploy, rollback or coordinated release is in progress, two releases can answer the same
address with different bytes. During that window no response becomes shared-cacheable, with one
exception: a generation-addressed tile, because the release revision is part of the generation, so
each release has its own addresses. When the operation completes, `/*` is invalidated.

## Consequences

- Every response carries `Cache-Control`. Routes that do not declare a class are `private, no-store`,
  which is safe and costs cache efficiency until they declare one.
- A route-table test fails if an admin route, an authorized route or a write route declares a public
  class, and sweeps every route with credentials to prove the guard.
- CloudFront must forward `g` in the cache key and must not cache on its own defaults. The
  distribution, the task-role permission and the Console's static assets are configured outside this
  repository.
- An anonymous caller that edits a public layer can read a feature query up to `s-maxage` old through
  the CDN. Layers that allow anonymous writes should set the feature query lifetime to 0.
