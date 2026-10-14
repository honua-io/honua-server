---
type: concept
title: "Task and operation"
description: "The user's desired work, distinguished from a callable process and a particular job execution."
tags: [gis, domain-language]
aliases: ["task","operation","process","workflow","request"]
sources:
  - id: contract
    resource: "/guides/query-analyze/run-geoprocessing.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/job.md"
  - predicate: relates_to
    target: "/concepts/domain/workflow-language.md"
  - predicate: relates_to
    target: "/concepts/domain/crs.md"
  - predicate: relates_to
    target: "/guides/workflows/analyze-features.md"
  - predicate: relates_to
    target: "/concepts/domain/capability.md"
---

# Task and operation
A task in ordinary language is a desired outcome, such as “find nearby facilities.” A process is an advertised operation with an input/output contract. A [job](job.md) is one execution. A [workflow](workflow-language.md) can connect several operations and decisions.

GPServer also uses “task” for a published process. Familiar aliases do not guarantee another system's parameter contract: the advertised process metadata determines the accepted inputs.[^contract]

“Run the analysis” requires concrete source resources, a spatial question, [CRS and units](crs.md), parameters, expected output type, and authority. It does not identify a tool solely by its name.

Use [analyze features](../../guides/workflows/analyze-features.md). Discovery, validation, asynchronous acceptance, and terminal success are different states. Resolve [capabilities](capability.md) before attempting execution.

[^contract]: [Source contract](../../guides/query-analyze/run-geoprocessing.md).
