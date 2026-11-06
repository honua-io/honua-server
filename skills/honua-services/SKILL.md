---
name: honua-services
description: Use when importing a small dataset, publishing it as a Honua layer, and checking a filtered feature query.
---

# Import, publish, and query

Read the install `AGENTS.md`. Call serving MCP at `http://127.0.0.1:<HONUA_HTTP_PORT>/mcp`. The install rules allow a script to read one named key file and not print it. Use that script to put `secrets/honua_admin_password` in the `X-API-Key` header. If `tools/list` lacks `honua_ingest_dataset` and `honua_publish_service`, reconnect with `?view=configure`.

1. For inline data of at most 4 MB, call `honua_ingest_dataset` with `format` `geojson` or `csv` and a `datasetName`. Keep `connectionId`, `schema`, `table`, `geometryColumn`, and `primaryKey` from the result. The same `datasetName` replaces that table. A null `connectionId` is not always a missing registration: read the warnings, then call `honua_admin_connections_list`. If a connection for this database is already listed, pass its name or id. If more than one matches, choose one. Do not create another. The same warning is used when a listed connection's credentials cannot be resolved, so register one only when that list is empty. If the list fails, stop and report that. `honua_admin_connections_create` refuses a password and this quickstart allows no request secret reference. Register with `honua-admin` (`pip install honua-admin==0.1.12`): `HonuaAdminClient.create_connection` and `CreateSecureConnectionRequest` with `name` `catalog`, `host` `postgres`, `port` 5432, `database_name` `honua`, `username` `honua`, `ssl_mode` `Disable`, and `ssl_required` false. Point the client at `http://127.0.0.1:<HONUA_HTTP_PORT>`. The same script may read `secrets/postgres_password` into `password` and `secrets/honua_admin_password` into the client. Print nothing from them. Pass the returned name as `connectionId`. Do not ingest again after registering.
2. Call `honua_publish_service` with `connectionId`, `schema`, `table`, and `layerName`. Pass `geometryColumn` and `primaryKey` when ingest returned them. `Completed` includes the published service and layer. `RequiresApproval` is not published; wait on its approval lane. `Queued` is a status. Poll `jobId` on that reply. `Accepted` has not finished.
3. If the reply is lost, read the operation, the proposal, and the job before publishing again. `honua_list_layers` omits an accepted, queued, or approval-pending publication, so absence there does not mean the publish failed or finished. Leave an unresolved outcome as uncertain. A second `honua_publish_service` creates another layer.
4. Verify with [a feature query](references/query.md).
5. Success is the new layer in `honua_list_layers` and a query whose features or `count` match the filter you sent. Say when the count could not be checked.

Common failure: missing tools means the configure view was not selected. HTTP 401 means authentication failed: the key was missing or invalid. Stop. A refusal for missing admin authority or scope is permanent; another view does not grant it. Stop. A timeout after ingest means the table is there. Publish from that table only after the operation, proposal, and job show that publication did not finish. Do not ingest it again.

Leave service access unchanged unless the user asked to change it.

Layer publication behaviour: [Publish layers](https://honua.gitbook.io/honuaio/work-with-your-data/publish-data/publish-layers).
