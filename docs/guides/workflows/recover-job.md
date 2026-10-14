---
type: "guide"
title: "Recover or cancel an interrupted job"
description: "Establish an interrupted execution's actual outcome and recover without duplicate effects."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "retry the job"
  - "continue processing"
  - "cancel the import"
  - "recover after restart"
sources:
  -
    id: "contract"
    resource: "/guides/query-analyze/run-geoprocessing.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Establish an interrupted execution's actual outcome and recover without duplicate effects."
  inputs:
    -
      name: "job"
      concept: "/concepts/domain/job.md"
      required: true
      description: "Known job identity, owner, tenant, attempt, and last observed state."
  outputs:
    -
      name: "job"
      concept: "/concepts/domain/job.md"
      description: "Observed outcome, including explicit failure or pending state."
      from: "$actions.recover.job"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "retry"
      interpretations:
        - "poll again"
        - "resume the known execution"
        - "create a new run"
      clarify: "What is known about the previous accepted operation's outcome?"
  actions:
    -
      id: "observe"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_list_jobs"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "observe_job"
      inputs:
        -
          name: "job"
          from: "$inputs.job"
          concept: "/concepts/domain/job.md"
      outputs:
        -
          name: "job"
          concept: "/concepts/domain/job.md"
          description: "Current durable state and execution evidence."
      effect: "observe"
      effectTarget: "observation"
      preconditions:
        - "The original durable job identity is retained."
      authorization:
        - "Read the job under its owner/tenant contract."
      idempotency: "Repeated state reads do not resubmit work."
      checks:
        - "Queued, running, successful, failed, and cancelled outcomes are distinguished."
        - "Worker and attempt evidence establish whether execution already occurred."
      recovery:
        - "If identity or outcome is unknown, report uncertainty and investigate instead of replaying."
    -
      id: "recover"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_cancel_job"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "recover_execution"
      inputs:
        -
          name: "job"
          from: "$actions.observe.job"
          concept: "/concepts/domain/job.md"
      outputs:
        -
          name: "job"
          concept: "/concepts/domain/job.md"
          description: "Confirmed terminal or resumed job with retained lineage."
      effect: "update"
      effectTarget: "job_state"
      preconditions:
        - "Recovery or cancellation is advertised and the observed state permits it."
      authorization:
        - "Control this job through its documented owner/tenant boundary."
      idempotency: "Use the runtime's retry/cancellation contract; never create an unrelated job as an invisible retry."
      checks:
        - "Cancellation is terminally confirmed or clearly remains pending."
        - "A recovered success has one intended final output and preserved attempt lineage."
      recovery:
        - "Wait for durable reconciliation where required; cleanup only after worker termination or terminal outcome."
---

# Recover or cancel an interrupted job

A dropped connection is not evidence that work failed. Start with the existing [job](../../concepts/domain/job.md) identifier and its durable record. Inspect ownership, state, attempts, and output references.

Follow the runtime's recovery or cancellation contract. A queued retry and a new independent execution are different events. If the prior outcome is unknown, establish it before repeating a write or creating another output.

A cancellation acknowledgment may only mean the request was accepted. Follow the job to a terminal state and confirm that the worker no longer writes outputs before cleanup.

A successful recovery must retain a retrievable output and honest attempt lineage. If dependencies or the durable runner are unavailable, report that limitation; do not simulate recovery by recreating state in local temporary files.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/query-analyze/run-geoprocessing.md) defines the executable surface.
