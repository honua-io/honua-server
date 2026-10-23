---
type: "guide"
title: "Connect and publish existing data"
description: "Expose an authorized dataset through a published layer and explicitly configured service."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "connect my database"
  - "publish a table"
  - "make a data service"
sources:
  -
    id: "contract"
    resource: "/reference/admin-api/connections-and-layers.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Expose an authorized dataset through a published layer and explicitly configured service."
  inputs:
    -
      name: "source"
      concept: "/concepts/domain/dataset.md"
      required: true
      description: "Named source table or raster with provenance and intended storage mode."
    -
      name: "connection"
      concept: "/concepts/domain/connection.md"
      required: true
      description: "Approved data-source endpoint and secret-reference mechanism."
    -
      name: "service"
      concept: "/concepts/domain/service.md"
      required: true
      description: "Intended service exposure, protocols, and audience policy."
  outputs:
    -
      name: "layer"
      concept: "/concepts/domain/layer.md"
      description: "Published data resource."
      from: "$actions.publish.layer"
    -
      name: "service"
      concept: "/concepts/domain/service.md"
      description: "Configured serving exposure."
      from: "$actions.expose.service"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "publish"
      interpretations:
        - "register a source-backed layer"
        - "create a managed copy"
        - "activate a map"
      clarify: "Which resource should become available, and should source changes remain live?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_admin_connections_test"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_source"
      inputs:
        -
          name: "source"
          from: "$inputs.source"
          concept: "/concepts/domain/dataset.md"
        -
          name: "connection"
          from: "$inputs.connection"
          concept: "/concepts/domain/connection.md"
      outputs:
        -
          name: "source"
          concept: "/concepts/domain/dataset.md"
          description: "Validated source, schema, CRS, and storage-mode decision."
      effect: "read"
      effectTarget: "source_data"
      preconditions:
        - "Source table is within the connection's allowed schema and host policy."
      authorization:
        - "Caller can inspect the source and connection."
      idempotency: "Read-only discovery."
      checks:
        - "Identity, schema, geometry, CRS, and provenance are unambiguous."
        - "Source-backed versus managed publication is explicitly selected."
      recovery:
        - "Resolve ambiguity or validation errors without altering source records."
    -
      id: "publish"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_admin_layer_publish"
          contract: "/guides/connect/ai-agents-mcp.md"
        -
          kind: "mcp"
          operation: "honua_publish_service"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "publish_layer"
      inputs:
        -
          name: "source"
          from: "$actions.inspect.source"
          concept: "/concepts/domain/dataset.md"
        -
          name: "connection"
          from: "$inputs.connection"
          concept: "/concepts/domain/connection.md"
      outputs:
        -
          name: "layer"
          concept: "/concepts/domain/layer.md"
          description: "Created or reconciled published layer."
      effect: "create"
      effectTarget: "catalog_configuration"
      preconditions:
        - "The publication contract and selected storage mode are supported."
      authorization:
        - "Caller may publish this source into the named scope."
      idempotency: "Inspect existing publication and returned operation identity before retrying."
      checks:
        - "Published binding matches the selected source or managed copy."
        - "Readable schema and data match the intended source; edit capabilities are not inferred."
      recovery:
        - "Remove only the newly created owned publication after dependency checks."
      rollback:
        snapshot: "Record the pre-change resource identifiers, state, revision, and dependencies."
        revisionGuard: "Re-read current state; stop compensation if the resource changed outside this action."
        scope: "The specific new publication and any separately identified owned managed copy."
        compensation: "Unpublish the new resource; preserve source data and pre-existing publications."
        verification:
          - "Read the affected resources again and verify the intended prior state under the same identity."
          - "Verify dependencies and access through the affected public surface."
        nonReversible:
          - "Managed-copy creation may consume storage; source changes during copying are not reversed."
    -
      id: "expose"
      interfaces:
        -
          kind: "http"
          operation: "Update protocols on the existing service"
          method: "PUT"
          path: "/services/{serviceName}/protocols"
          contract: "/developer/api-specs/admin-api.json"
      verb: "configure_service"
      inputs:
        -
          name: "layer"
          from: "$actions.publish.layer"
          concept: "/concepts/domain/layer.md"
        -
          name: "service"
          from: "$inputs.service"
          concept: "/concepts/domain/service.md"
      outputs:
        -
          name: "service"
          concept: "/concepts/domain/service.md"
          description: "Service exposing the layer through permitted protocols."
      effect: "update"
      effectTarget: "catalog_configuration"
      preconditions:
        - "Audience and enabled protocols are explicit."
        - "Publication already associates the resource with the intended service; this step changes only that service's protocol settings."
      authorization:
        - "Caller may change this service; access-policy changes require their own authority."
      idempotency: "Reconcile the service revision; never overwrite unrelated layers."
      checks:
        - "The intended endpoint resolves the correct layer."
        - "Service and layer restrictions remain effective."
      recovery:
        - "Restore the recorded protocol configuration if its revision is still owned."
      rollback:
        snapshot: "Record the pre-change resource identifiers, state, revision, and dependencies."
        revisionGuard: "Re-read current state; stop compensation if the resource changed outside this action."
        scope: "Only the named service's changed protocol settings."
        compensation: "Restore the recorded service configuration without changing data policies implicitly."
        verification:
          - "Read the affected resources again and verify the intended prior state under the same identity."
          - "Verify dependencies and access through the affected public surface."
        nonReversible:
          - "Clients may have observed the temporary exposure."
---

# Connect and publish existing data

Start by resolving the [dataset](../../concepts/domain/dataset.md), [connection](../../concepts/domain/connection.md), and destination [service](../../concepts/domain/service.md). Testing a connection does not publish its tables.

Choose storage behavior deliberately. A source-backed layer retains the source table as authority. Managed publication copies records into a managed store, which becomes authoritative for that published layer. Source-backed publication does not supply feature writes. This workflow publishes data; it does not authorize AI edits of source records.

Validate fields, geometry, CRS, source rights, and the intended read policy. Publish the selected table into the intended service once, then configure only the needed protocol exposure. Changing the catalog does not itself imply anonymous access.

The publish action lists alternative interfaces. `honua_admin_layer_publish` and `honua_publish_service` both publish from connection/schema/table context; they are not a two-call chain. In particular, `honua_publish_service` does not accept an existing layer identifier to attach it to a service. The separate protocol-update binding uses the Admin OpenAPI server base `/api/v1/admin` plus its relative path and changes the existing service's enabled protocols.

Verify the returned identifiers and query the intended endpoint as an intended reader. If the caller asks to “undo,” distinguish unpublishing a newly created layer from deleting data. Preserve shared resources and source tables.

The service/result publication tools document non-idempotent behavior: replay can create another layer. Persist returned identifiers and reconcile catalog and operation state after a timeout. A familiar request body is not an idempotency key.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../reference/admin-api/connections-and-layers.md) defines the executable surface.
