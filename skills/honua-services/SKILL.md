---
name: honua-services
description: Use when importing data, publishing a Honua service or layer, and verifying a filtered feature query without duplicating a lost publish.
---

# Import, publish and verify a service

Read the install's `AGENTS.md`. Discover the serving MCP tools and their schemas.
For administration use the `configure` workflow view if available. Discover
connections and source tables before asking the operator for ids.

1. Import through the available ingest/import tool. For small inline datasets,
   `honua_ingest_dataset` returns the source identity used by publishing.
   Preserve the returned connection, schema, table, geometry column, primary key
   and spatial reference. Wait for asynchronous imports to complete. Some import
   flows publish automatically: inspect their result before publishing again.
2. Publish an unpublished source with `honua_publish_service` or
   `honua_admin_layer_publish`, whichever the server advertises for the task.
   Copy source identity and field metadata from discovery/import results. Follow
   the live schema rather than substituting a display name for an id.
3. Treat a publish handle's `RequiresApproval` or a proposal's `AwaitingApproval`
   as pending; a `Queued` publish handle identifies a job to poll. A request or accepted proposal is not a published service. Let a separate
   approver act when required.
4. On a lost response, read back the operation, job, proposal and published layer
   inventory before retrying. Publishing is not inherently idempotent: a repeated
   `honua_publish_service` can create another layer.
5. Discover and describe the published layer. Build the requested filter from
   its actual field names and types, then run `honua_query_features` with bounded
   fields and page size. Read [query verification](references/query.md).
6. Report the service/layer identifiers and exactly the result you verified.
   Leave any access restriction until verification is finished. Change service
   access only if the user asked for it.

See the [layer concept](https://honua.io/docs/concepts/domain/layer/) in the OKF documentation.
