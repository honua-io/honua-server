---
type: concept
title: "Export"
description: "A Dataset written as cloud-native files in immutable Versions, with one current Version and signed-in or public access that follows its Layer."
tags: [gis, domain-language]
aliases: ["export","cloud-native files","share as files","download files","PMTiles","GeoParquet","COG","paired publication"]
sources:
  - id: contract
    resource: "/reference/admin-api/exports.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/dataset.md"
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/artifact.md"
  - predicate: relates_to
    target: "/concepts/domain/sharing.md"
  - predicate: relates_to
    target: "/concepts/domain/approval.md"
  - predicate: relates_to
    target: "/guides/workflows/share-data-as-files.md"
---

# Export

An **Export** is a [Dataset](dataset.md) written as cloud-native files that any client can read without a Honua server in the request. Vector data becomes a GeoParquet file for query and download and a PMTiles archive for display; raster data becomes a Cloud Optimized GeoTIFF (COG). An Export is made from a [Layer](layer.md), so it uses that Layer's fields, identity and access rules.[^contract]

## Versions and Artifacts

Each run of an Export writes a new **Version**. A Version never changes after it is written: its files are the Export's [Artifacts](artifact.md), and one STAC collection document, `collection.json`, describes them. The document follows the open Portolan specification, so a STAC or Portolan client opens the same URL as the Honua SDKs. Artifact links in the document are relative, so one document works at every address the Version is served from.

The GeoParquet file is the `data` asset and the PMTiles archive is the `visual` asset. Every tile feature carries the identity field, so a feature clicked on the map resolves to exactly one GeoParquet row. Facts only Honua needs, such as that identity field and the public field list, sit in a small `honua:` extension.

## The current Version

The Export's **current pointer** names one Version. It moves only after every Artifact has been written and read back with a matching checksum, and the source did not change during the read. A failed or interrupted run leaves the previous Version current. Clients that need stable bytes keep the Version URL; clients that want the newest data read the current pointer.

## Who can read it

Access follows the Layer. A signed-in reader with export access to the Layer reads every Version through the Honua API, and each read rechecks that access, so hiding a field or retiring the source makes older Versions unavailable too.

When the Layer is public and an anonymous reader would see every row and every field, Honua also copies the current Version to the installation's public data path. When the Layer stops being public or the Export is unpublished, Honua deletes those public files. An Export with a row filter, hidden fields or tenancy stays on the signed-in routes, and the response says why. A URL grants nothing by itself; see [Sharing](sharing.md).

## Exports and agents

Creating or unpublishing an Export from an agent returns a [Proposal](approval.md); a person approves it before the job runs. A person's own call starts the job directly.

## Not an Export

- A one-off download in CSV, GeoJSON or another format is a [Job](job.md) result, not an Export: it has no Versions and no current pointer.
- A tile cache or seeded tiles serve a Service; they are not files a client can take away.
- Activating a saved Map or App Version is [Go live](publication.md). Earlier releases called an Export a "paired publication"; that name is retired.

Follow [share data as files](../../guides/workflows/share-data-as-files.md).

[^contract]: [Exports API](../../reference/admin-api/exports.md).
