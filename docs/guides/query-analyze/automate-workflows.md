# Automate workflows

Chain geoprocessing steps into a declarative DAG, publish it on a cron schedule, and watch runs — the orchestration engine handles step wiring, retries, and crash recovery.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) with Redis configured — the orchestration stores and scheduler only register when a Redis connection is available — and an admin API key ([authentication](../secure/authentication.md)).

A workflow package is a graph of nodes (each node is a catalog process, node type `process:{processId}`) connected by data edges that pipe one step's output artifact into the next step's input. Packages are drafted, versioned immutably, validated, dry-run, and then published to a schedule. Also available in Honua Console — UI guide coming soon.

## Steps

1. Browse the node registry to see what you can chain — every entry mirrors a geoprocessing catalog process:

   Open Honua Console's Workflow Builder and inspect the node palette. It reads `/api/v1/console/workflow-node-registry`; Console routes are not exposed by the OpenAPI explorer.

2. Create a package with a two-step graph: buffer a geometry, then simplify the buffered result. The data edge's `targetPort` names the downstream input (`wkb`) that the upstream artifact fills; node `parameters` are strings:

   Run `POST /api/v1/console/workflow-packages` with this body:

   ```json
   {
     "name": "buffer-then-simplify",
     "graph": {
       "schemaVersion": "workflow-package.v1",
       "nodes": [
         {
           "nodeId": "buffer",
           "nodeTypeId": "process:geometry.buffer",
           "parameters": {
             "wkb": "AQEAAABQ/Bhz15pewNDVVuwv40JA",
             "srid": "4326",
             "distance": "500"
           }
         },
         {
           "nodeId": "simplify",
           "nodeTypeId": "process:geometry.simplify",
           "parameters": { "srid": "4326", "tolerance": "10" }
         }
       ],
       "edges": [
         {
           "sourceNodeId": "buffer",
           "targetNodeId": "simplify",
           "kind": "Data",
           "targetPort": "wkb"
         }
       ]
     }
   }
   ```

   Note the package id (`PKG` below) from the response.

3. Snapshot the draft as an immutable version, then validate and dry-run it before publishing:

   Run `POST /api/v1/console/workflow-packages/{packageId}/versions`, followed by the `/versions/1/validate` and `/versions/1/dry-run` operations.

4. Publish version 1 to a schedule. Data-wired graphs must publish to the `Schedule` target so the orchestration engine can chain step outputs; the cron expression is 5-field, with an optional IANA time zone (default UTC):

   Run `POST /api/v1/console/workflow-packages/{packageId}/versions/1/publish` with this body:

   ```json
   {
     "target": "Schedule",
     "schedule": {
       "cronExpression": "0 6 * * *",
       "timeZone": "UTC"
     }
   }
   ```

   The scheduler evaluates cron triggers every 30 seconds and claims each fire-time so exactly one replica creates a run per occurrence. Failed steps retry per the engine's per-step retry policy with exponential backoff; see [operations](../deploy/backup-and-restore.md#workflow-orchestration) for run lifecycle, failure policies, and crash-safety details.

5. Trigger a run on demand and watch it. The run id doubles as an operation id on the admin progress API:

   Run `POST /api/v1/console/workflow-publications/{publicationId}/runs` with `{}`, then use the returned `workflowRunId` in `GET /api/v1/admin/operations/{runId}`.

   `GET /api/v1/console/workflow-publications` lists publications; `GET /api/v1/admin/operations/active` lists in-flight runs; `POST /api/v1/admin/operations/{runId}/cancel` cancels a run (cascading to its child jobs).

## Verify

Run `GET /api/v1/admin/operations/{runId}` again in the explorer.

Expected (trimmed): run progress that ends in a succeeded state with both steps terminal.

```json
{ "data": { "operationId": "…", "operationType": "Orchestration", "status": "Succeeded" } }
```

## Troubleshoot

- **503 on orchestration operations** — no Redis connection: the orchestration engine and scheduler are not hosted in Redis-less deployments. Configure Redis and restart.
- **400 on publish: `cannot resolve cross-node data bindings`** — graphs with data edges must use `"target": "Schedule"`, not `Job` or `ProcessEndpoint`.
- **Validation errors name `steps[simplify].inputs.wkb`** — a required input is neither a literal parameter nor fed by a data edge; add the edge `targetPort` or the parameter.
- **Schedule never fires** — invalid cron expressions or unknown time zones are skipped and logged at warning (event 8116); fix the expression. Cron is 5-field (`min hour dom mon dow`).
- **Run stuck with warnings about job observation** — transient Redis reads are retried each reconcile tick; persistent warnings point at Redis health. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Run geoprocessing](run-geoprocessing.md)
- [Operations: workflow orchestration](../deploy/backup-and-restore.md#workflow-orchestration)
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md)
