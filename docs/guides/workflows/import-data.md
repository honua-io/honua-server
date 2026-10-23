---
type: "guide"
title: "Import data with explicit geometry and provenance"
description: "Create a retained imported dataset with checked schema, geometry, and provenance."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "load a file"
  - "import CSV"
  - "upload GeoJSON"
  - "bring in data"
sources:
  -
    id: "contract"
    resource: "/guides/publish/import-files.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Create a retained imported dataset with checked schema, geometry, and provenance."
  inputs:
    -
      name: "source"
      concept: "/concepts/domain/dataset.md"
      required: true
      description: "Input data, format, encoding, source rights, and identifier fields."
    -
      name: "reference"
      concept: "/concepts/domain/crs.md"
      required: true
      description: "Coordinate reference, axis order, units, and coordinate-field mapping."
    -
      name: "destination"
      concept: "/concepts/domain/storage.md"
      required: true
      description: "Authorized new output location and retention requirements."
  outputs:
    -
      name: "dataset"
      concept: "/concepts/domain/dataset.md"
      description: "Retained imported dataset; publication is a separate action."
      from: "$actions.import.imported"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "coordinates"
      interpretations:
        - "longitude and latitude"
        - "latitude and longitude"
        - "projected easting and northing"
      clarify: "Which columns, order, CRS, and units describe the input?"
    -
      phrase: "load"
      interpretations:
        - "preview"
        - "import records"
        - "add an existing layer to a map"
      clarify: "Should a new durable dataset be created?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "http"
          operation: "Preview input data"
          method: "POST"
          path: "/import/preview"
          contract: "/developer/api-specs/admin-api.json"
      verb: "validate_input"
      inputs:
        -
          name: "source"
          from: "$inputs.source"
          concept: "/concepts/domain/dataset.md"
        -
          name: "reference"
          from: "$inputs.reference"
          concept: "/concepts/domain/crs.md"
      outputs:
        -
          name: "validated"
          concept: "/concepts/domain/dataset.md"
          description: "Input with an explicit interpretation and validation decision."
      effect: "read"
      effectTarget: "source_data"
      preconditions:
        - "Input is readable within authorized network/file scope."
      authorization:
        - "Read source data and metadata."
      idempotency: "Read-only profiling."
      checks:
        - "Encoding, quoting, nulls, numeric types, feature identities, and geometry are interpreted explicitly."
        - "Null, empty, invalid, and mixed geometry policies are stated."
      recovery:
        - "Report malformed input and request a decision; do not silently repair authoritative source data."
    -
      id: "import"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_admin_import_upload_url"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "import_dataset"
      inputs:
        -
          name: "source"
          from: "$actions.inspect.validated"
          concept: "/concepts/domain/dataset.md"
        -
          name: "destination"
          from: "$inputs.destination"
          concept: "/concepts/domain/storage.md"
      outputs:
        -
          name: "imported"
          concept: "/concepts/domain/dataset.md"
          description: "New owned imported dataset, after confirmed completion."
      effect: "create"
      effectTarget: "derived_artifact"
      preconditions:
        - "The importer supports the format and declared coordinate interpretation."
        - "The output is a new owned dataset; it does not overwrite the authoritative source."
      authorization:
        - "Create in the named output scope."
      idempotency: "Use the returned import identity and inspect its outcome before resubmission."
      checks:
        - "Terminal completion is observed when the import is asynchronous."
        - "Output schema, values, counts, geometry bounds, and provenance match the interpretation."
      recovery:
        - "Inspect a pending job after interruption; remove only an owned failed output after terminal confirmation."
      rollback:
        snapshot: "Record the pre-change resource identifiers, state, revision, and dependencies."
        revisionGuard: "Re-read current state; stop compensation if the resource changed outside this action."
        scope: "The new imported dataset and temporary artifacts only."
        compensation: "Delete the newly created output after confirming no retained publications depend on it."
        verification:
          - "Read the affected resources again and verify the intended prior state under the same identity."
          - "Verify dependencies and access through the affected public surface."
        nonReversible:
          - "Source input is unchanged; accepted downstream copies cannot be recalled automatically."
---

# Import data with explicit geometry and provenance

Inspect the file before importing it. For CSV, identify coordinate columns, delimiter, quoting, encoding, null representation, and field types. For geometry formats, establish the actual coordinate contract. A file suffix alone does not establish an arbitrary CRS.

Standard GeoJSON uses longitude/latitude geographic coordinates; other CRS arrangements require an explicitly supported contract. Never repair a projected dataset merely by relabeling its coordinates. Preserve the original source and produce a separate converted output when transformation is required.

Choose an authorized destination and verify the importer and runner are available. An accepted background job must reach a terminal result before the imported dataset is called complete. [Job recovery](recover-job.md) establishes an uncertain outcome without duplicate imports.

Use a fresh isolated destination and record ownership. The staged dataset ingestion tool can replace an existing staged dataset when its name is reused, so its name is not a safe retry key. URL import has a separate documented `Idempotency-Key` contract scoped to actor, tenant, and effective request; do not transfer that guarantee to another import interface.

The HTTP binding's path is relative to the Admin OpenAPI server base `/api/v1/admin`. The MCP URL-import binding is for an authorized supported URL; it is not a file-upload binding. Compose calls using the selected interface's exact input schema.

Inspect the complete result, including pagination when exposed, representative attributes, geometry bounds, and source identifiers. Invalid input should produce a clear diagnosis or an explicit user decision. Import does not automatically grant access or add the result to a map; continue with [publication](connect-publish-data.md) or [composition](compose-map.md) as requested.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/publish/import-files.md) defines the executable surface.
