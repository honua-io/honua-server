---
type: concept
title: "DevOps workflow language"
description: "Meanings, state transitions, and recovery boundaries for operating a Honua environment through ordinary language."
tags: [gis, domain-language, operations]
aliases: ["upgrade my server", "roll back", "deploy", "configuration drift", "GitOps"]
sources:
  - id: deployment
    resource: /guides/deploy/upgrade-and-rollback.md
  - id: recovery
    resource: /reference/versioning-and-support.md
  - id: metadata
    resource: /reference/control-plane-migration-guide.md
relations:
  - predicate: specializes
    target: /concepts/domain/workflow-language.md
  - predicate: operates_on
    target: /concepts/domain/deployment.md
  - predicate: distinguishes
    target: /concepts/domain/studio-workspace.md
---
# DevOps workflow language

"Upgrade my server," "why is this job stuck?", and "put the previous configuration back" express different goals. This vocabulary makes their objects, intended changes, and evidence explicit. It uses the existing deployment, GitOps, history, and recovery interfaces; it introduces no new deployment engine.

## Objects and transitions

| Term | Required meaning |
| --- | --- |
| Target | A named environment and concrete serving or execution backend, with tenant and operator scope |
| Desired state | The versioned configuration intended for that target; an instruction is not evidence it was applied |
| Observed state | A timestamped observation from the actual target, including uncertainty and drift |
| Release | A declared platform release whose serving and worker artifacts come from its signed release lock |
| Plan | A comparison of desired and observed state, affected resources, prerequisites, and recovery scope |
| Apply | An authorized operation against a particular plan and revision, with a returned identity |
| Converge | Bring eligible serving targets to the already declared release using the current control plane contract |
| Verify | Observe the required behavior and dependent services after the change and during its observation window |
| Roll back | Request a specific supported recovery to a prior compatible revision, then verify the result |

The general sequence is **observe → compare → plan → apply → observe → verify**. A drift conflict returns to observation; it does not authorize overwriting another operator's change. Acceptance, proposal approval, rollout completion, and successful verification are distinct states. Preserve the actual interface's state names and identifiers rather than inventing one universal status enum.

## Compose through observed interfaces

The [upgrade contract](../../guides/deploy/upgrade-and-rollback.md) names the current interfaces. `honua_platform_release_status` reads the declared release. `GET /api/v1/admin/deploy/preflight` supplies readiness and compatibility observations. `honua_propose_platform_release_convergence` proposes convergence to that declaration; it does not resolve a requested version into an image. Follow the returned proposal and operation identifiers, then observe the deployed target. Use the contract's existing approval boundary for these operational proposals.

This boundary does not belong in the [Studio prompt and preview loop](studio-workspace.md). A routine map style adjustment can preview immediately within the authorized workspace. Deploying a server or changing an audience uses its own existing policy.

Check runtime discovery and schemas before composing calls. A semantic verb and a documentation link are insufficient to construct an argument object. A complete binding needs the installed interface identity/version, required parameters, returned identifier paths, error meanings, retry behavior, and relevant recovery operation. CLI spelling must come from the actual command contract. An unavailable adapter remains an implementation gap.

For metadata, follow the [current control-plane migration guide](../../reference/control-plane-migration-guide.md): GitOps release packages replace removed manifest-apply and watch routes. Old documentation or a familiar command name must not revive a retired interface.

## Recovery has a scope

Application rollback, configuration history, map undo, cancellation, and database recovery are separate operations. Reverting an image does not undo a migration. [Versioning and support](../../reference/versioning-and-support.md) describes the compatibility and backup requirements; do not infer a schema downgrade command.

Record the prior revision, changed resource identities, expected current revision, recovery mechanism, and verification checks before changing operational state. After an uncertain response, reconcile the returned operation or target state before retrying. A timeout is not evidence that nothing happened. Recovery must preserve unrelated resources and concurrent work.

AI workflows never edit authoritative feature attributes or geometry. Neither operational authority nor a rollback label creates an exception. Source-data remediation produces a report or recommendation for direct human control.

## Hosting distinctions that affect meaning

ECS, EKS, and Lambda are serving targets with different rollout mechanisms. Batch is an execution backend, not a map-serving endpoint. A serving rollout does not prove an already running job switched worker images. The current convergence contract explicitly defers workers to subsequent geoprocessing dispatch. Keep job identity and ownership through controller recovery; do not submit a second job merely because observation was interrupted.

"Make it healthy" requires an observable target: readiness, a query, a durable job, or another explicit user outcome. One successful readiness response does not prove all GIS workflows function.

## Scenarios that define the language

- "Upgrade staging to the declared release": verify the declared artifact and scope, follow the existing proposal/apply path, and observe the outcome.
- "Production changed since the plan": detect drift and recompute; preserve the intervening edit.
- "The deploy request timed out": reconcile its identity and state before any retry.
- "Put the old version back": distinguish a map version, deployment revision, metadata revision, and database restore before choosing an operation.
- "Lambda is healthy but my import disappeared": inspect durable runner, job, and storage capabilities instead of treating request success as durable completion.
- "Restart the controller while Batch is working": retain and observe the original provider job rather than resubmit it.

These are semantic acceptance scenarios, not claims of completed tests. Workflow descriptions and static graph checks must be paired with independent runtime observations for qualification.
