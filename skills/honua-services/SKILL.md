---
{
  "name": "honua-services",
  "description": "Use when importing a small dataset, publishing it as a Honua layer, and checking a filtered feature query."
}
---

# Import, publish, and query

Read the install `AGENTS.md`. Call serving MCP at `http://127.0.0.1:<HONUA_HTTP_PORT>/mcp` with a human-provisioned agent credential. Follow [agent key provisioning](https://honua.gitbook.io/honuaio/drive-honua-with-an-agent/honua-server/ai-agents-mcp#connect-a-client): the human creates the key outside the agent session, with `agent`, administrative read/write grants for this task, and data-read grants limited to the intended services. Use the install's protected credential channel without printing the key. Do not read bootstrap administrator or database passwords, create your own key, or use an unmarked administrator credential. If the credential is missing or lacks a required grant, stop and request that credential or grant from the human. If `tools/list` lacks `honua_ingest_dataset` and `honua_publish_service`, reconnect with `?view=configure`; discovery does not grant authority.

1. For inline data of at most 4 MB, call `honua_ingest_dataset` with `format` `geojson` or `csv` and a fresh `datasetName`. Keep `connectionId`, `schema`, `table`, `geometryColumn`, and `primaryKey` from the result. To change an existing dataset, use the staged-import operation (`honua_op_dataset_stage_import`) and its human approval flow; if unavailable, stop and ask the human to perform the change. Do not retry the create-only ingest against an existing table. A null `connectionId` is not always a missing registration: read the warnings, then call `honua_admin_connections_list`. Use a listed connection only when its database match is unambiguous. If multiple connections match, stop and ask the human to select one. If none matches or credentials cannot be resolved, ask the human to register or repair the connection in Console. Do not read database passwords to register it yourself. If listing fails, stop and report that. After the human resolves registration, use that connection with the retained ingest result; do not ingest again.
2. Call `honua_publish_service` with `connectionId`, `schema`, `table`, and `layerName`. Pass `geometryColumn` and `primaryKey` when ingest returned them. `Completed` includes the published service and layer. `RequiresApproval` is not published; retain its operation and proposal references and ask the human to review that exact proposal in Console Approvals. Client consent or a conversational “yes” does not approve it. Follow the same proposal to completion before reporting publication. `Queued` is a status. Poll `jobId` on that reply. `Accepted` has not finished.
3. If the reply is lost, read the operation, the proposal, and the job before publishing again. `honua_list_layers` omits an accepted, queued, or approval-pending publication, so absence there does not mean the publish failed or finished. Leave an unresolved outcome as uncertain. A second `honua_publish_service` creates another layer.
4. Verify with [a feature query](references/query.md).
5. Success is the new layer in `honua_list_layers` and a query whose features or `count` match the filter you sent. Say when the count could not be checked.

Other tasks have their own views: reconnect with `?view=metadata` to read or edit service and layer metadata (descriptions, service discovery metadata and time info), `?view=access` for API keys, roles, grants and service access policy, `?view=exports` to export a Layer as files, and `?view=folders` to organize folders. An agent's metadata or access change files a proposal; follow it to completion as in step 2.

Common failure: missing tools means the configure view was not selected. HTTP 401 means authentication failed: the key was missing or invalid. Stop. A refusal for missing admin authority or scope is permanent; another view does not grant it. Stop. An ingest timeout leaves the outcome uncertain and returns no operation identity to poll. Stop and report that supported verification is needed before continuing; do not ingest it again or publish from that table. For a submitted publication request that returned an operation, proposal, or job identity, retain that identity and inspect its status before continuing.

Leave service access unchanged unless the user asked to change it.

Layer publication behaviour: [Publish layers](https://honua.gitbook.io/honuaio/work-with-your-data/publish-data/publish-layers).
