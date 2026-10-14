---
type: concept
title: "Job and result"
description: "A particular execution with ownership, state, attempts, and result artifacts."
tags: [gis, domain-language]
aliases: ["background task","processing job","run","queued import","cancel"]
sources:
  - id: contract
    resource: "/reference/admin-api/imports-and-jobs.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/task.md"
  - predicate: relates_to
    target: "/concepts/domain/deployment.md"
  - predicate: relates_to
    target: "/concepts/domain/storage.md"
  - predicate: relates_to
    target: "/guides/workflows/recover-job.md"
---

# Job and result
A job is an execution record for accepted work. It has an identifier and status; it may also expose progress, a runner, and output references. Acceptance or “processing” is not successful completion.[^contract]

An import job and a geoprocessing job have their own contracts. A [task](task.md) definition can be executed many times, so its name is not a job identifier. A retry attempt is also not permission to create duplicate final outputs.

Durable imports use shared state so their outcome survives an accepting server restart. Availability depends on the actual [deployment](deployment.md), Redis, and compatible runners. A terminal result reference must point to retained [storage](storage.md); an ephemeral worker file is insufficient.

Use [recover a job](../../guides/workflows/recover-job.md). Cancellation requests must be followed to a terminal outcome. Avoid blind resubmission when the previous outcome is unknown.

[^contract]: [Source contract](../../reference/admin-api/imports-and-jobs.md).
