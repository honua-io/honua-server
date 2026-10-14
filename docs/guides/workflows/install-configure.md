---
type: "guide"
title: "Install and configure a durable environment"
description: "Run a verified Honua serving environment with the persistence and job capabilities the intended work needs."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "install Honua"
  - "start my server"
  - "configure storage"
sources:
  -
    id: "contract"
    resource: "/guides/deploy/cloud-deployments.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Run a verified Honua serving environment with the persistence and job capabilities the intended work needs."
  inputs:
    -
      name: "deployment"
      concept: "/concepts/domain/deployment.md"
      required: true
      description: "Authorized target, immutable artifact, desired configuration, and permitted resource scope."
    -
      name: "storage"
      concept: "/concepts/domain/storage.md"
      required: true
      description: "Existing or authorized new database, durable job store, and output storage requirements."
  outputs:
    -
      name: "deployment"
      concept: "/concepts/domain/deployment.md"
      description: "Verified environment and capability limitations."
      from: "$actions.verify.verified"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "serverless"
      interpretations:
        - "Lambda serving"
        - "remote background execution"
        - "scale-to-zero infrastructure"
      clarify: "Which work must survive a request ending or a serving host restart?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_list_capabilities"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_target"
      inputs:
        -
          name: "deployment"
          from: "$inputs.deployment"
          concept: "/concepts/domain/deployment.md"
        -
          name: "storage"
          from: "$inputs.storage"
          concept: "/concepts/domain/storage.md"
      outputs:
        -
          name: "target"
          concept: "/concepts/domain/deployment.md"
          description: "Validated target and required dependency plan."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Identify ECS, EKS, Lambda, or local serving and any separate job backend."
      authorization:
        - "Operator may inspect the named environment."
      idempotency: "Read-only; refresh observations before apply."
      checks:
        - "Artifact family matches the hosting target."
        - "Network, credentials, persistent state, and cleanup scope are explicit."
      recovery:
        - "Resolve missing provisioning authority or dependencies before applying."
    -
      id: "configure"
      verb: "configure_deployment"
      inputs:
        -
          name: "target"
          from: "$actions.inspect.target"
          concept: "/concepts/domain/deployment.md"
      outputs:
        -
          name: "running"
          concept: "/concepts/domain/deployment.md"
          description: "Deployment after the authorized configuration change."
      effect: "update"
      effectTarget: "deployment"
      preconditions:
        - "An approved operator plan exists where policy requires it."
      authorization:
        - "Provisioning identity is limited to the named environment."
      idempotency: "Use the deployment system's declared operation identity; inspect an uncertain apply before retry."
      checks:
        - "Readiness and the actual serving artifact match the intended configuration."
        - "Effective capabilities report the expected persistence and worker availability."
      recovery:
        - "Restore the recorded deployment configuration only after checking drift."
      rollback:
        snapshot: "Record the pre-change resource identifiers, state, revision, and dependencies."
        revisionGuard: "Re-read current state; stop compensation if the resource changed outside this action."
        scope: "Only the named deployment configuration and newly allocated owned resources."
        compensation: "Reapply the recorded compatible configuration through the normal operator control plane."
        verification:
          - "Read the affected resources again and verify the intended prior state under the same identity."
          - "Verify dependencies and access through the affected public surface."
        nonReversible:
          - "Database/schema changes and source data are not undone by an image rollback."
          - "External requests and billing already incurred remain."
    -
      id: "verify"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_ops_health"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "verify_persistence"
      inputs:
        -
          name: "deployment"
          from: "$actions.configure.running"
          concept: "/concepts/domain/deployment.md"
      outputs:
        -
          name: "verified"
          concept: "/concepts/domain/deployment.md"
          description: "Environment with observed readiness and persistence checks."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Use the configured normal runtime identity."
      authorization:
        - "Read-only verification access to the installed environment."
      idempotency: "Repeat reads without reprovisioning."
      checks:
        - "Configured state remains retrievable across the intended host lifecycle."
        - "Durable operations are reported unavailable when the required runner or storage is absent."
      recovery:
        - "Record unsupported operations explicitly; do not replace durable behavior with local temporary state."
---

# Install and configure a durable environment

Choose the serving target and the lifecycle the work needs. [Deployment](../../concepts/domain/deployment.md) separates the HTTP host from job execution; [storage](../../concepts/domain/storage.md) separates source data, catalog state, jobs, and artifacts.

Inspect the pinned artifact and dependency requirements before provisioning. ECS and EKS serve containers; Lambda has a target-specific artifact and request lifecycle. Batch can execute jobs for a serving host, but is not the map endpoint. Record what is actually configured rather than inferring capability from a cloud product name.

Apply only within the authorized environment. Preserve a configuration snapshot, revision, and ownership inventory. Verify readiness, the serving identity, and persistence through the lifecycle users expect. A restart check must retrieve previously committed state, not recreate it.

Rollback restores a compatible deployment configuration through its normal control plane. It does not reverse database migrations, source data changes, requests already served, or charges. Stop on drift or an incompatible storage/schema transition and report the specific recovery decision needed.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/deploy/cloud-deployments.md) defines the executable surface.
