---
type: reference
title: "Feature locks (collaborative editing)"
description: "A feature lock is a short lease one editor takes on one feature so a second editor cannot overwrite it mid-edit."
---
# Feature locks (collaborative editing)

A feature lock is a short lease one editor takes on one feature so a second editor cannot
overwrite it mid-edit. Honua's leases are **enforced on every feature-write path**: while
you hold a lease, a competing `applyEdits`, OGC API Features `PUT`/`PATCH`/`DELETE`,
transaction batch operation, or OData update/delete on that feature is refused and your
stored row is left exactly as you left it.

Enforcement is not per protocol handler. Every protocol adapter that mutates features
funnels through one shared edit pipeline, and the lease is checked there as well as in the
handlers — so surfaces without their own check (WFS-T, the gRPC feature service, the OData
atomic change-set) cannot be used to route around a lease, and neither can a write path
added later.

Read this page with the [saved-map collaboration op-log](../saved-map-collaboration-op-log.md),
which covers the durable ordering of saved-map document edits. Feature locks cover the
underlying feature data.

## What is enforced in 2026.1

| Question | 2026.1 answer |
| --- | --- |
| Are leases enforced on writes? | **Yes** — FeatureServer `applyEdits`, OGC API Features writes and OData writes all consult the lease store before mutating a feature. |
| Is claiming a lease mandatory? | **No.** The enforced policy is *honour an active lease*: an edit is refused only while **another** editor holds one. A client that never claims a lease behaves exactly as before. |
| Are expected-version tokens enforced on GeoServices `applyEdits`? | **No — not implemented.** Optimistic concurrency on that surface is provided by the server-side precondition path, not by a client-supplied version token. OGC API Features enforces `If-Match`/`412` on `PUT`, merge-`PATCH` and `DELETE`. |
| Are leases enforced across nodes? | **No — not implemented.** The only lease store that ships is in-process. See [Single-node scope](#single-node-scope). |
| Is lease authorization configured out of the box? | **No.** The shipped authorizer denies every claim, so until you supply your own no lease is granted at all (see [Authorization](#authorization)). |
| Is a lease atomic with the write it guards? | **No.** The check happens immediately before the mutation, not inside the writer transaction. See [What a lease does not promise](#what-a-lease-does-not-promise). |

These are the recorded dispositions for honua-server#4402.

**Three of them are machine-readable.** The rows below are published by
`GET /api/v1/capabilities/manifest`, so a client can branch on them without reading this
page:

| Manifest capability | What it reports |
| --- | --- |
| `collaboration.feature-locks` | `supported: true` — enforcement ships on every feature-write path. `available` is `true` only once you supply an [authorizer](#authorization); until then it is `false` with `reasonCode: disabled-by-configuration`, because a lease that can never be granted is not an available capability. |
| `collaboration.feature-locks.cross-node` | `supported: false`, `lifecycle: planned`, `reasonCode: unsupported` — leases do not span nodes. See [Single-node scope](#single-node-scope). |
| `edit.geoservices-version-tokens` | `supported: false`, `lifecycle: planned`, `reasonCode: unsupported` — the GeoServices `applyEdits` surface honours no client-supplied version token. The row is named for that surface on purpose: OGC API Features **does** enforce `If-Match`/`412`, and a generically-named row would tell you to disable optimistic concurrency where it works. |

That is the same shape the manifest already uses for an unimplemented file-format writer,
so a client that can read one can read these.

**Two of them are not, and are documented here only.** Whether claiming a lease is
mandatory, and whether the lease check is atomic with the write it guards, are semantics of
how enforcement behaves rather than capabilities that can be switched on or off; the
manifest has no field that could carry them without inventing one. Read
[What a lease does not promise](#what-a-lease-does-not-promise) for both.

One further caveat when you read the manifest: `capabilities[].available` does not account
for a [deployment capability profile](../../guides/deploy/capability-deployment-profiles.md). A profile that omits
`collaboration.map-sessions` removes the feature-lock routes, and no capability row in the
manifest folds that in — the profile is published separately as `deploymentProfile`, and a
client that cares must intersect the two. That is uniform across every row in the document,
not specific to feature locks.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim` | Take (or renew) a lease. |
| `POST` | `/api/v1/saved-maps/{mapId}/collaboration/feature-locks/renew` | Extend a lease you hold. |
| `POST` | `/api/v1/saved-maps/{mapId}/collaboration/feature-locks/release` | Give a lease back. |

Request body:

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `serviceName` | string | yes | The service **name** from the URL you edit through — see [Naming a feature](#naming-a-feature). |
| `layerId` | int | yes | The layer id from that same URL. |
| `featureId` | string | yes | The feature's `OBJECTID`. |
| `holderId` | string | yes | Your editor label. Not a credential — see [Proving you are the holder](#proving-you-are-the-holder). |
| `displayName` | string | no | Shown to the editor who gets blocked. |
| `sessionId` | string | no | Editing session. When set, a write must present the same session id to count as yours. |
| `tenantId` | string | no | Tenant scope. |
| `leaseSeconds` | int | no | 1–3600, default 120. |

A claim on a feature someone else holds returns `409` with the current holder and the
lease expiry, so your client can prompt ("locked by Alice until 14:32").

## Naming a feature

A lease is keyed on `(serviceName, layerId, featureId)`.

- `serviceName` is the service **name** — the segment in
  `/rest/services/{serviceName}/FeatureServer/{layerId}`, not an internal graph id. Matched
  case-insensitively: `Parcels` and `parcels` are the same lease.
- `layerId` is the published layer id in that same route.
- `featureId` is the feature's `OBJECTID`. Numeric ids are normalised, so `007` and `7` are
  the same lease; an opaque non-numeric identifier is compared verbatim after trimming.

Using the routed name is what makes one lease cover every protocol: a layer published as a
FeatureServer layer and as an OGC API Features collection resolves to the same
`(name, layerId)` pair, so a lease claimed once blocks a competing write arriving on either
surface. If a layer publishes a custom public identifier that is not its `OBJECTID`, claim
the lease with the `OBJECTID`.

## Proving you are the holder

A write is recognised as *yours* when it comes from **the authenticated principal that
claimed the lease**. The server records that principal on the lease itself; no client can
set or change it. This is what makes ownership unforgeable — everything else about a lease
is caller-chosen and is echoed back to the editor who gets blocked, so a conflict response
would otherwise be a recipe for impersonating the holder.

The headers below are labels that select *which of your own leases* a write belongs to.
Copying another editor's values into them does not make you that editor:

| Header | Meaning |
| --- | --- |
| `X-Honua-Lock-Holder` | The `holderId` you claimed the lease under. Defaults to your authenticated principal name when the header is absent. |
| `X-Honua-Lock-Session` | The `sessionId` you claimed under, when you set one. |
| `X-Honua-Lock-Tenant` | The `tenantId` you claimed under, when you set one. |

Claim under your authenticated principal name and you never need to send a header. Claim
under any other `holderId` — an application-level editor id, a session-scoped id — and your
own writes must echo it back, or you will be blocked by your own lease.

A request whose identity cannot be determined at all is never the holder, so any active
lease blocks it. That is deliberate: an unidentified caller must not be able to walk past
the one control the holder was given.

## What a lease does not promise

A lease is a coordination primitive you claim *before* you begin editing, not a database
lock. Two limits follow, and neither is hidden from you:

- **It is not atomic with the write.** The lease is evaluated immediately before the
  mutation, not inside the writer's transaction. A lease claimed after a competing request
  has already started may not stop that request. Claim before you edit and the window does
  not arise; if you need a hard serialisation point, use the optimistic-concurrency
  preconditions (`If-Match` on OGC API Features and OData) which *are* re-validated inside
  the write transaction.
- **It is not a permission.** A lease says "someone else is editing this right now"; it
  does not grant, widen or narrow anyone's authorization. Every write still passes the
  usual RBAC, ownership and row-level-security checks first, and a lease never reveals a
  row you could not otherwise read.

## What a blocked write looks like

| Surface | Response |
| --- | --- |
| GeoServices `applyEdits` | HTTP `200` with the standard edit envelope; the blocked slot has `"success": false` and error code **`1005`** (`FeatureLocked`), and the description names the holder. With `rollbackOnFailure=true` the whole batch is rejected. |
| OGC API Features `PUT` / `PATCH` / `DELETE` | HTTP `423 Locked`, RFC 7807 problem with `type: https://honua.io/problems/feature-locked`. |
| OGC API Features transaction batch | The blocked operation fails with status `423`; the batch always runs with rollback, so no row in it is written. |
| OData update / delete | HTTP `423` with OData error code `FeatureLocked`. |

Creates are never blocked: a feature that does not exist yet cannot be leased.

## Single-node scope

`InMemoryFeatureLockService` is the only lease store that ships. Leases live in the memory
of the node that granted them, which has two consequences you must plan for:

- **A lease does not survive a restart.** After a node restarts, its leases are gone and the
  features are editable again.
- **A lease does not cross nodes.** In a multi-node deployment, a lease held on node A does
  not block a write routed to node B.

If you need leases to hold across a cluster, either pin collaborative editing sessions to a
single node with sticky routing, or supply your own `IFeatureLockService` backed by a shared
store. A distributed lease store is not part of 2026.1.

## Authorization

The `IFeatureLockAuthorizer` that ships (`FailClosedFeatureLockAuthorizer`) denies every
claim: an unauthenticated caller gets `401`, an authenticated one gets `403` with
"Feature lock authorization is not configured." Per-map editing ACLs are not part of
2026.1, so a deployment that wants collaborative locking supplies its own authorizer. The
enforcement described on this page applies to whatever leases that authorizer lets through.

`IFeatureLockAuthorizer` is public API in `Honua.Core.Features.Collaboration.FeatureLocks`,
so a downstream host or plugin assembly can implement it without rebuilding the server:

```csharp
internal sealed class MapEditorLockAuthorizer(IMapPermissions permissions) : IFeatureLockAuthorizer
{
    public async ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId, FeatureRef feature, ClaimsPrincipal principal, string operation, CancellationToken ct)
        => await permissions.CanEditAsync(mapId, principal, ct)
            ? FeatureLockAuthorizationResult.AllowWrite()
            : FeatureLockAuthorizationResult.Forbid("You are not an editor of this map.");
}

// Registered before the server's own fail-closed default, which uses TryAdd.
builder.Services.AddSingleton<IFeatureLockAuthorizer, MapEditorLockAuthorizer>();
```
