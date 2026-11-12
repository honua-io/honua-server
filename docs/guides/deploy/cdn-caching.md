---
type: guide
title: "Cache behind a CDN"
description: "What Honua lets a CDN store, for how long, how to change it for one layer or in an emergency, and when the CDN is told that content changed."
---
# Cache behind a CDN

On the supported AWS deployment CloudFront sits in front of the service. Honua decides what the CDN may store: every response carries a `Cache-Control` header, and the CDN is configured to follow it.

**Prerequisites:** A running deployment behind CloudFront, and access to its environment variables.

## What is cached

A response is public only when the route is a read of public content, the request is anonymous, and the layer allows anonymous access. Everything else is sent as `private, no-store` and is never stored by the CDN.

| Response | Browser keeps it | CDN keeps it | Notes |
| --- | --- | --- | --- |
| Tile of a public layer | 60 s | 300 s | May be served stale for 60 s while it is refreshed, and for a day if the service is unreachable |
| Feature query on a public layer | 0 s (revalidates) | 30 s | May be served stale for 30 s while it is refreshed, and for a day if the service is unreachable |
| Capabilities and metadata documents, TileJSON, style documents | 30 s | 60 s | |
| Scene assets of a public scene | 60 s | 300 s | The scene's own cache setting replaces these values |
| Anything requested with a credential, a secured layer, admin routes, edits | never | never | `private, no-store` |
| Errors | never | never | `private, no-store` |

WMS and WMTS key-value requests, OGC API Maps, static map images, exports, imported tile packages, and feature queries by an arbitrary extent or spatial predicate are not cached in this release.

A request counts as credentialed when it carries any of: the `Authorization`, `X-API-Key`, `X-Esri-Authorization`, `X-Honua-Embed-Key`, `X-Honua-Token`, `X-Honua-Tenant` or `X-Honua-Job-Id` header, a forwarded client certificate, an admin session cookie, or a `token` or `key` query parameter.

## When the CDN is told that content changed

| Change | What is invalidated | When |
| --- | --- | --- |
| A new release starts, or a release is rolled back | Everything | When the first task of that release starts |
| A layer, service or scene is published, unpublished, enabled, disabled, secured or made public | Everything | Before the admin request returns |
| A style or a read policy changes | Everything | Before the admin request returns |
| An operator invalidates the cache | Everything | Before the admin request returns |
| Data is edited through Honua | That layer's tiles and queries, and the OGC API surfaces | At once for the first edit, then at most once every 30 seconds |

If you edit a table outside Honua, with a desktop GIS or a load script, the server does not see the change. Tiles and queries refresh after the layer's cache lifetime, or immediately when you invalidate the layer's cache.

CloudFront limits how many invalidations may be in progress. When it is busy the service retries for up to 17 seconds; when many layers are edited in the same 30 seconds it invalidates everything once instead of naming each layer.

An admin request that told the CDN carries the response header `Honua-Cdn-Invalidation: submitted`, or `failed` when CloudFront refused. A failure never undoes the change and never blocks a rollback; a release operation records it in the operation's warnings. Until the invalidation is repeated, cached responses expire with their lifetime.

Every invalidation is logged once as a purge record (event 10311): the reason (`admin`, `edit`, `release` or `operation`), the change it belongs to, the task that sent it, the CloudFront outcome and invalidation id, and the paths. For an admin change, the change is the `X-Correlation-ID` returned to the admin request, so you can find the purge a given change caused.

## The server's own cache on several tasks

Pro editions also keep cached metadata, tiles and styles in each task's memory. Each entry is keyed by the catalog revision, so after a change on any task every other task stops replaying its entries within the revision check interval (`Cache__MetadataGraphRevalidationSeconds`, default 2 s). An entry for a layer, service, collection or scene is stored only after its access check allowed an anonymous caller, and no entry lives longer than the CDN lifetime of its response, so a task never hands the CDN an older copy after a purge.

Responses on cached routes carry an RFC 9211 `Cache-Status` header for this cache:

| Value | Meaning |
| --- | --- |
| `Honua; hit; ttl=240; detail="revision=..."` | Replayed from the task's cache; the entry lives 240 s more |
| `Honua; fwd=miss; detail="revision=..."` | Rendered for this request |
| `Honua; fwd=bypass` | Not looked up: the request carries a credential, is signed in, is not a plain GET, or the cache is not available in this edition |

The revision is the catalog revision the response was keyed by; two tasks that answer with different revisions have not both seen the latest change yet.

## Steps

1. Tell the service which distribution to invalidate. Without this setting nothing is invalidated and lifetimes are the only control.

```bash
SharedCache__CloudFront__DistributionId=E2EXAMPLE123
```

The task role needs `cloudfront:CreateInvalidation` on that distribution only.

2. Change a product default if your data changes faster or slower than the defaults assume.

```bash
SharedCache__Tile__MaxAgeSeconds=60
SharedCache__Tile__SharedMaxAgeSeconds=300
SharedCache__FeatureQuery__SharedMaxAgeSeconds=30
SharedCache__MetadataDocument__SharedMaxAgeSeconds=60
```

3. Give one layer its own lifetime. Layers are named `{serviceId}/{layerId}`, as in `/rest/services/{serviceId}/FeatureServer/{layerId}`; the name applies to that layer on every surface, including `/tiles` and OGC API Tiles. A layer bound to a table that is edited outside Honua should have a lifetime no longer than the staleness you accept.

```bash
SharedCache__Layers__parcels/3__MaxAgeSeconds=0
SharedCache__Layers__parcels/3__SharedMaxAgeSeconds=15
```

4. In an emergency, shorten every public layer to 15 seconds at the CDN and 0 in the browser in one step. Stored per-layer lifetimes are not changed, and unsetting the value restores them.

```bash
SharedCache__Emergency__AllPublicLayers=true
```

To shorten only some layers, name them instead:

```bash
SharedCache__Emergency__Layers__0=parcels/3
SharedCache__Emergency__Layers__1=roads/1
```

## Verify

- Request a public tile without credentials and check that `Cache-Control` starts with `public, max-age=60, s-maxage=300`.
- Request the same tile with an API key and check that `Cache-Control` is `private, no-store`.
- Secure a layer, then request it anonymously through the CDN: the request is refused.
- Request a public tile twice from the same task and check that `Cache-Status` goes from `Honua; fwd=miss` to `Honua; hit`.

## Troubleshoot

- **Everything is `private, no-store`.** The request carries a credential or names a tenant, or a development bypass identity is active. None of these is shared.
- **A change is not visible through the CDN.** Check the admin response for `Honua-Cdn-Invalidation: failed`, and the task role's permission on the distribution.
- **Edits made outside Honua are not visible.** Shorten the layer's lifetime, or invalidate the layer's cache after loading data.
