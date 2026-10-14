---
type: concept
title: "Artifact and output reference"
description: "A produced file, geometry result, image, or package, distinct from its publication."
tags: [gis, domain-language]
aliases: ["output", "result file", "download", "generated file"]
sources:
  - id: persistence
    resource: /user/gp-output-store-attestation.md
relations:
  - predicate: produced_by
    target: /concepts/domain/job.md
  - predicate: depends_on
    target: /concepts/domain/storage.md
  - predicate: disambiguates
    target: /concepts/domain/layer.md
---
# Artifact and output reference

An artifact is a produced output, such as a converted file, preview image, derived dataset, or package. An output reference locates that artifact; it is useful only while the artifact remains retrievable with the intended identity.

Referenced geoprocessing artifacts require the documented persistent output store. A worker's temporary file is not a durable deliverable.[^persistence] The execution [job](job.md) and the output have distinct lifetimes.

“Publish the result” is additional work. A file might first need import or registration as a [layer](layer.md), then exposure through a [service](service.md), then a reference in a [map](map.md). A completed conversion does not prove those later states.

Check content, format, provenance, ownership, and retrieval before handing over a result. Use [run a native job](../../guides/workflows/run-native-job.md) and [cleanup](../../guides/workflows/cleanup-resources.md).

[^persistence]: [Referenced output persistence](../../user/gp-output-store-attestation.md).
