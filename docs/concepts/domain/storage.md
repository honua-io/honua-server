---
type: concept
title: "Persistent storage"
description: "The durability and ownership of data, catalog state, jobs, and output artifacts."
tags: [gis, domain-language]
aliases: ["save","disk","volume","database","Redis","bucket","output store"]
sources:
  - id: contract
    resource: "/user/gp-output-store-attestation.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/dataset.md"
  - predicate: relates_to
    target: "/concepts/domain/job.md"
  - predicate: relates_to
    target: "/guides/workflows/save-resume-map.md"
  - predicate: relates_to
    target: "/guides/workflows/install-configure.md"
  - predicate: relates_to
    target: "/guides/workflows/cleanup-resources.md"
---

# Persistent storage
Storage has several roles: source [datasets](dataset.md), managed data, catalog and Studio state, durable [jobs](job.md), and output artifacts. These need not live in the same system.

Referenced geoprocessing outputs require a persistent output store with the documented provisioning and recovery evidence. Serving hosts and workers need compatible access to that store; worker ephemeral storage is not a supported durable output store.[^contract]

“Saved” should identify which object was committed, where it is retrievable, and whether its dependencies survive restart. A browser cache, local temporary file, or running process is not proof of durability.

Use [save and resume a map](../../guides/workflows/save-resume-map.md) for content, [install and configure](../../guides/workflows/install-configure.md) for operator persistence, and [cleanup](../../guides/workflows/cleanup-resources.md) for resource ownership. Retention, backup, and deletion are separate decisions.

[^contract]: [Source contract](../../user/gp-output-store-attestation.md).
