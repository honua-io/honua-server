---
type: concept
title: "Deployment and execution target"
description: "Where Honua serves requests and where background work executes."
tags: [gis, domain-language]
aliases: ["install","host","environment","ECS","EKS","Lambda","Batch"]
sources:
  - id: contract
    resource: "/guides/deploy/cloud-deployments.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/publication.md"
  - predicate: relates_to
    target: "/operator/geoprocessing-aws-batch.md"
  - predicate: relates_to
    target: "/concepts/domain/native-worker.md"
  - predicate: relates_to
    target: "/guides/workflows/install-configure.md"
  - predicate: relates_to
    target: "/concepts/domain/capability.md"
---

# Deployment and execution target
A deployment combines the serving artifact, configuration, network, secrets, persistent services, and operational identity. Installing a process is not the same as [publishing data](publication.md).

ECS/Fargate runs the serving container; EKS runs it in Kubernetes. Lambda uses its target-specific serving image and request lifecycle. AWS Batch is a job execution backend, not the HTTP map server. A serving host can submit work to a separate Batch runner.[^contract]

The [AWS Batch guide](../../operator/geoprocessing-aws-batch.md) distinguishes managed execution workers from [native workers](native-worker.md). Having Batch configured does not prove native conversion is available.

Follow [install and configure](../../guides/workflows/install-configure.md). Choose an immutable compatible artifact and inspect effective [capabilities](capability.md). Lambda's durable operations depend on its configured remote runner and storage; do not equate “serverless” with durable background execution or with a blanket lack of that capability.

[^contract]: [Source contract](../../guides/deploy/cloud-deployments.md).
