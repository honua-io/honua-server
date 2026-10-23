---
type: "guide"
title: "Run a native conversion and retain its output"
description: "Execute a supported native operation on the selected worker and retrieve the genuine retained result."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "convert with GDAL"
  - "run conversion in Batch"
  - "process a large file"
sources:
  -
    id: "contract"
    resource: "/guides/deploy/cloud-deployments.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Execute a supported native operation on the selected worker and retrieve the genuine retained result."
  inputs:
    -
      name: "source"
      concept: "/concepts/domain/dataset.md"
      required: true
      description: "Authorized input data and requested conversion semantics."
    -
      name: "worker"
      concept: "/concepts/domain/native-worker.md"
      required: true
      description: "Compatible immutable native worker artifact and execution contract."
    -
      name: "storage"
      concept: "/concepts/domain/storage.md"
      required: true
      description: "Shared input/output access and persistence requirements."
  outputs:
    -
      name: "artifact"
      concept: "/concepts/domain/artifact.md"
      description: "Checked native output; publication is separate."
      from: "$actions.execute.result"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "Batch conversion"
      interpretations:
        - "a native ETL workload"
        - "a managed import worker"
        - "a local process invoked from a Batch wrapper"
      clarify: "Which native process, image, and provider execution must produce the output?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_list_capabilities"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_native_execution"
      inputs:
        -
          name: "worker"
          from: "$inputs.worker"
          concept: "/concepts/domain/native-worker.md"
        -
          name: "storage"
          from: "$inputs.storage"
          concept: "/concepts/domain/storage.md"
      outputs:
        -
          name: "worker"
          concept: "/concepts/domain/native-worker.md"
          description: "Verified native execution target."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Queue, job definition, role, worker image, and input/output access are inspectable."
      authorization:
        - "Read the execution configuration without exposing secrets."
      idempotency: "Read-only validation."
      checks:
        - "The selected runtime actually provides the native process."
        - "Native conversion is distinguished from a managed import or publication worker."
      recovery:
        - "Report missing provenance or runtime support before executing."
    -
      id: "execute"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_validate_plan"
          contract: "/guides/connect/ai-agents-mcp.md"
        -
          kind: "mcp"
          operation: "honua_execute_plan"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "execute_native_conversion"
      inputs:
        -
          name: "source"
          from: "$inputs.source"
          concept: "/concepts/domain/dataset.md"
        -
          name: "worker"
          from: "$actions.inspect.worker"
          concept: "/concepts/domain/native-worker.md"
        -
          name: "storage"
          from: "$inputs.storage"
          concept: "/concepts/domain/storage.md"
      outputs:
        -
          name: "result"
          concept: "/concepts/domain/artifact.md"
          description: "Native output with job, worker, and source provenance."
      effect: "execute"
      effectTarget: "derived_artifact"
      preconditions:
        - "The process and format conversion are supported and non-destructive."
        - "The output is a new owned artifact."
      authorization:
        - "Read input, submit the named workload, and write only the approved output scope."
      idempotency: "Track the runtime job/attempt identity and inspect unknown outcomes before retry."
      checks:
        - "A real worker execution reaches terminal success."
        - "Output bytes and format are retrievable from persistent storage."
        - "The result retains expected records, geometry/reference, schema, and attribution."
      recovery:
        - "Use documented job cancellation/recovery; retain source input unchanged."
---

# Run a native conversion and retain its output

Identify the conversion semantics and the actual native process. The web serving image and the native ETL image are distinct. Selecting AWS Batch alone does not prove that a native conversion runs there.

Inspect the immutable worker artifact, job definition, role, and shared storage arrangement. A managed large import is useful but does not substitute for execution of the requested native process.

Execute against retained input and write a separate owned output. Confirm the provider job and worker evidence, terminal success, and retrievable bytes. Check meaningful format and geometry properties, not merely an output filename.

Continue with [publication](connect-publish-data.md) and [map composition](compose-map.md) only when requested. Keep those additional resources and their cleanup separate from the conversion job.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/deploy/cloud-deployments.md) defines the executable surface.
