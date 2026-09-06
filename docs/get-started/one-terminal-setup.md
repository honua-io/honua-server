# One-terminal setup journey

For a native PowerShell customer install, use
[Windows: install published packages](windows-packages.md), including an
import/publish/query fixture and restart verification. The source validation
journey below is a separate contributor workflow.

This is the executable part of the terminal-first setup journey. It starts a
clean local Honua candidate, publishes and verifies a layer, discovers the
server-authored MCP setup view, and runs bounded geoprocessing. Stop after the
GP result today: the saved map/dashboard to governed-publication remainder is
not yet an executable candidate path.

> [!IMPORTANT]
> **Candidate truth.** The stages marked **execution-verified** were replayed
> against source revision `ddf373e86` on 1 September 2026. A tool appearing in
> the `setup` view proves discovery only. It does not prove that its downstream
> dependency, authorization path, or approval bridge is ready.

## 1. Install, publish, and verify — execution-verified

Follow [Quickstart: zero to a map](quickstart.md) from a clean checkout of the
pinned revision:

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server
git checkout ddf373e86
HONUA_DOCS_PRESERVE_STACK=1 bash scripts/docs-validation/validate-quickstart.sh
```

The validation script extracts the commands from the quickstart instead of
maintaining a second recipe. It starts clean PostGIS, Redis, and server
containers; creates the connection; publishes the sample table; queries its
three features; and retrieves TileJSON plus a non-empty MVT. The preservation
flag leaves that validated stack and its database volume running so the later
stages can use the returned service and layer IDs. Do not copy the quickstart
development credential into a deployed environment.

The quickstart currently installs the pinned Python SDK packages from the
`python-sdk-v0.1.9` source tag. The server image is built from the checked-out
revision rather than from a floating registry tag.

## 2. Select the bounded setup view — execution-verified

Connect an MCP client to `POST /mcp` as described in [Connect AI agents](../guides/connect/ai-agents-mcp.md).
Discover the views by calling `honua_list_capabilities`, then request the
server-authored setup view rather than carrying a client-owned tool list:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/list",
  "params": { "view": "setup" }
}
```

The candidate returns the complete `setup.v1` projection in one page, bounded
to at most 48 descriptors and 128 KiB. Preserve its `revisionDigest`,
`membershipDigest`, and `descriptorDigest` with the run receipt. The view
includes the canonical descriptors for setup, style/render, GP, Studio, and
publication stages, but every call is still independently authenticated and
authorized. Descriptor membership is not an execution receipt.

## 3. Run bounded GP — execution-verified

Continue with [Run geoprocessing](../guides/query-analyze/run-geoprocessing.md).
The verified candidate path discovers `geometry.buffer`, executes it through
OGC API Processes, waits on the returned job URL, and reads the result. The
same operation can be submitted through `honua_validate_plan` and
`honua_execute_plan`; poll `honua://jobs/{jobId}` and read the results resource.
Retain the job ID and output artifact reference.

After retaining the receipt, remove the local stack and its volumes:

```bash
docker compose --project-name honua-docs-quickstart down --volumes --remove-orphans
```

Direct analysis-profile verbs such as `buffer_features` remain intentionally
absent until
[#3269](https://github.com/honua-io/honua-server/issues/3269) delivers their
implementations.

## Stop here: exact blocked remainder

Do not turn the following descriptors into a claimed end-to-end walkthrough
yet:

- **Canonical style and render receipt:** the server tools exist, but
  [honua-sdk-js#1401](https://github.com/honua-io/honua-sdk-js/issues/1401)
  still blocks the required zero-to-map profile/catalog preflight and proof
  that the published layer renders with the applied canonical style.
- **Saved and reopened map/dashboard:** map draft mechanics exist, but
  [#3429](https://github.com/honua-io/honua-server/issues/3429) still blocks
  dashboard drafts across the MCP lifecycle. The server-discovered SDK routing
  and lifecycle/status client remain open in
  [honua-sdk-js#1397](https://github.com/honua-io/honua-sdk-js/issues/1397) and
  [honua-sdk-js#1398](https://github.com/honua-io/honua-sdk-js/issues/1398).
- **Governed Studio publication:** `honua_studio_propose_publication` records
  intent only. [#3304](https://github.com/honua-io/honua-server/issues/3304)
  still blocks the bridge from that intent to the canonical proposal/approval
  lifecycle. The typed deploy and release proposal tools do not substitute for
  a Studio publication proposal.
- **Separate-principal approval and replay:** the durable canonical operation
  envelope/bridge remains open in
  [#3411](https://github.com/honua-io/honua-server/issues/3411); OAuth identity
  binding and scope preservation remain open in
  [#3430](https://github.com/honua-io/honua-server/issues/3430) and
  [#3431](https://github.com/honua-io/honua-server/issues/3431). The focused
  `admin:approve` key recipe is separately blocked in
  [#3365](https://github.com/honua-io/honua-server/issues/3365).
- **Clean-machine model canary and cloud parity:**
  [#3428](https://github.com/honua-io/honua-server/issues/3428) has landed the
  bounded server-authored view mechanics, but its issue remains blocked pending
  the genuine unforced terminal-model canary. This local receipt therefore does
  not claim the certified AWS ECS DevOps handoff.

The eventual complete receipt must join the candidate revision, deployment,
connection, service/layer, style/render artifact, GP job/result, Studio draft
generation and saved version/hash, proposal and canonical operation/audit IDs,
approver principal, and final public URL. Until every join is executable, this
page deliberately ends at the GP artifact.
