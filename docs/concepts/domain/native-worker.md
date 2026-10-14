---
type: concept
title: "Managed and native workers"
description: "Execution artifacts with distinct runtime capabilities, separate from the serving host."
tags: [gis, domain-language]
aliases: ["worker","GDAL","OGR","conversion","ETL worker","Batch image"]
sources:
  - id: contract
    resource: "/guides/deploy/cloud-deployments.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/job.md"
  - predicate: relates_to
    target: "/operator/geoprocessing-aws-batch.md"
  - predicate: relates_to
    target: "/guides/workflows/run-native-job.md"
---

# Managed and native workers
A managed execution worker can run a compatible job executor from the serving-image family. A native ETL worker is a separate artifact providing native conversion dependencies. The production native-AOT web image excludes GDAL/OGR and its native libraries.[^contract]

AWS Batch is a [job](job.md) backend; selecting it does not by itself select a native image. The [Batch guide](../../operator/geoprocessing-aws-batch.md) describes managed workers and large layer publication. A large import and a native file conversion prove different behavior.

For “convert this file in Batch,” resolve the actual process, worker artifact, queue/job definition, role, input location, and persistent output access. Verify which runner executed the job and retrieve the produced artifact.

Use [run a native job](../../guides/workflows/run-native-job.md). Do not substitute a server-side format response or local file copy for native execution.

[^contract]: [Source contract](../../guides/deploy/cloud-deployments.md).
