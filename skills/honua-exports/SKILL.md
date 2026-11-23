---
{
  "name": "honua-exports",
  "description": "Use when exporting a Honua layer as cloud-native files (PMTiles and GeoParquet), handling approval and job status, and verifying signed-in or public CDN URLs."
}
---

# Export a Layer as cloud-native files

Read the install `AGENTS.md`. Use a human-provisioned agent credential through the install's protected credential channel without printing the key. Do not read bootstrap administrator or database passwords, create your own key, or change Layer access policy unless the user explicitly asked to change it.

1. Identify the managed source Layer (`serviceId` and `layerId`). If the data is still a local file, import and publish it into managed storage first (see `honua-services`). Exports require a Layer backed by Honua's managed feature table; external database tables are refused.
2. Start the Export through operation `admin.exports.create` (discovered via MCP `tools/list` or `POST /api/v1/operations/admin.exports.create/submit`; administrators use `POST /api/v1/admin/exports`) with `source: { "kind": "layer", "serviceId": "<serviceId>", "layerId": <layerId> }`. Pass `exportId`, `identityField`, `minZoom`, `maxZoom`, or `maxTiles` only when overriding defaults. Do not call the retired tile-operations `paired-publish` action, and do not call mutating `/api/v1/admin/*` routes directly with an agent credential.
3. Respect the approval gate. Both `admin.exports.create` and `admin.exports.delete` require human approval when called by an agent. When the server returns `AwaitingApproval`, stop and give the human the exact proposal ID to review in Console Approvals. A conversational "yes" does not approve a proposal, and a pending proposal is not a completed Export.
4. After approval (or when polling an accepted job), read `admin.exports.get` (`GET /api/v1/admin/exports/{exportId}`) until `status` is `ready` or `failed`. Never report success while `status` is `running`, and never submit a second export job while one is active (`409 Conflict`). If a response is lost, inspect `admin.exports.list` (`GET /api/v1/admin/exports`) and the job status before retrying.
5. Verify the resulting URLs and assets using [verify export readback](references/verify.md).
6. To remove an Export, submit `admin.exports.delete` (via MCP or `POST /api/v1/operations/admin.exports.delete/submit`; administrators use `DELETE /api/v1/admin/exports/{exportId}`), wait for human approval, and confirm `status` is `unpublished` and any prior `publicUrl` is no longer served.

Common failures: HTTP 401 or 403 means missing credentials or export authority — stop and report it. A populated `notPublicReason` with no `publicUrl` means `FileStorage:PublicData` is off or the Layer failed the anonymous-safety gate (multi-tenancy, non-anonymous access, row filter, hidden field, or field mask) — report the exact `notPublicReason` instead of claiming public URLs exist or relaxing Layer access on your own.

Reference: [Export concept](https://honua.gitbook.io/honuaio/evaluate-honua/honua-server/concepts/domain/export) and [Share parcels as files](https://honua.gitbook.io/honuaio/work-with-your-data/publish-data/export-as-files).
