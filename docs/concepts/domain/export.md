---
type: concept
title: "Export"
description: "Cloud-native vector files (PMTiles and GeoParquet) produced from a Dataset in immutable Versions, described by one STAC collection document."
tags: [gis, domain-language]
aliases: ["export as files", "cloud-native export", "file export", "static export", "vector pair"]
sources:
  - id: guide
    resource: "/guides/publish/export-as-files.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/dataset.md"
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/artifact.md"
  - predicate: relates_to
    target: "/concepts/domain/job.md"
  - predicate: relates_to
    target: "/concepts/domain/sharing.md"
  - predicate: relates_to
    target: "/concepts/domain/approval.md"
  - predicate: relates_to
    target: "/guides/publish/export-as-files.md"
---

# Export

An **Export** publishes a [Dataset](dataset.md) as cloud-native files: a **vector pair** (PMTiles for display and GeoParquet for query and download, both written from one consistent capture). Use **Export** for cloud-native file outputs rather than the retired term "paired publication"; a [Layer](layer.md) exposes a Dataset in a Service, and [Go live](publication.md) activates a saved Map or App Version.[^guide]

## Versions, Artifacts, and the current pointer

Each export [Job](job.md) writes a new immutable **Version** containing its [Artifacts](artifact.md) and one STAC 1.1 collection document (`collection.json`) that follows the open Portolan specification:

- **Primary artifacts**: the `geoparquet` asset (role `data`, Hilbert-ordered and Zstandard-compressed) and the `pmtiles` asset (role `visual`, plus a `pmtiles` link), each carrying media type, `file:size`, and a multihash `file:checksum`.
- **Companion files**: generated from the Layer's metadata — `README.md`, `AGENTS.md` (public fields, CRS, and ready-to-run queries), `style.json` (MapLibre GL style derived from the Layer's style), and `thumbnail.png` when rendered.
- **Honua extension**: Honua-specific facts (such as the identity field linking a tile feature to its GeoParquet row and the capture identifier) live in the `honua:` STAC extension so standard Portolan and STAC clients read the collection without Honua-specific software.
- **Relative asset links**: Every asset href in `collection.json` is relative to the document, so the same file works unchanged on signed-in routes and public CDN paths.

After writing all files, the job moves the Export's **`current` pointer** (`current.json`) with a conditional write only when every check passes: stored bytes match the generated SHA-256 digests, the Layer and its metadata did not change during capture, and `collection.json` satisfies cross-field validation. If any check fails or another run moved the pointer first, the new Version is deleted and the previous Version stays current. Earlier Versions remain immutable, and an Export identifier stays bound to the Layer it first exported.

## Signed-in and public access

Every Export is readable on its signed-in route, and public CDN delivery follows the Layer's access safely:

- **Signed-in access**: Authenticated `GET` or `HEAD` requests to `/api/v1/exports/{exportId}/current.json` return a `307` redirect to the current immutable Version's `collection.json`. Resolve relative asset links against that immutable URL so both files stay on the same capture if a newer Version activates. Every read enforces current Layer export authorization, tenant isolation, and visible-field rules; because immutable archives cannot apply per-request row filters or field masks, reads return `403` when the current policy requires either. Responses carry `private, no-store`.
- **Public access**: When `FileStorage:PublicData` is enabled and the Layer passes the anonymous-safety gate (multi-tenancy off, unscoped resource and service, anonymous read and export allowed, no permanent row filter, no anonymous row-level security filter, no hidden schema field, and no anonymous field mask), Honua copies the Version's artifacts (`public, max-age=31536000, immutable`), then `collection.json`, and updates the public root `data/catalog.json` (`public, max-age=60, s-maxage=300`) last. A canary read checks that the public URL is reachable. If the Layer is ineligible or public data storage is disabled, the Export stays on signed-in routes and reports `notPublicReason`.
- **Revocation and unpublish**: If the Layer stops being publicly eligible or an administrator unpublishes the Export (`DELETE /api/v1/admin/exports/{exportId}`), Honua deletes the public objects, rewrites `data/catalog.json`, and tracks a CDN invalidation to completion.

## Governance

Creating (`admin.exports.create`) and unpublishing (`admin.exports.delete`) an Export are governed control-plane operations. When an agent submits either operation, Honua returns a [Proposal](approval.md) awaiting human approval in Console Approvals; a pending Proposal does not start a job or remove public files.

Follow [Share parcels as files](../../guides/publish/export-as-files.md) for the end-to-end workflow.

[^guide]: [Share parcels as files](../../guides/publish/export-as-files.md).
