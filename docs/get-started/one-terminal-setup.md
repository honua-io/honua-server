---
type: guide
title: "Honua from one terminal"
description: "Use one terminal workspace to install Honua, configure a service, apply a canonical style, run GP, author a map/dashboard and request governed publication."
---
# Honua from one terminal

Use one terminal workspace to install Honua, configure a service, apply a
canonical style, run GP, author a map/dashboard and request governed
publication. The terminal client owns the only model session. DevOps MCP
provisions and operates infrastructure; Admin CLI and server MCP configure
resources; server Studio tools own draft state. Browser clients are optional.

For a native PowerShell customer install, use
[Windows: install published packages](windows-packages.md), including an
import/publish/query fixture and restart verification. That guide covers the
Windows package installation path; the full release journey below retains its
candidate replay and publication requirements.

> **Pre-cut guide.** This page is not an execution receipt. Exact package and
> image pins must come from the signed 2026.1 platform lock, still tracked by
> [release #231](https://github.com/honua-io/honua-release/issues/231).
> The publication bridge remains open in
> [#3304](https://github.com/honua-io/honua-server/issues/3304).
> The supported source contracts and blocked stages are distinguished below;
> do not report a final URL until the canonical publication operation supplies
> one. See the [acceptance disposition](../internal/contributor/terminal-docs-precut-evidence.md).

## 1. Install and verify the handoff

Before running either path, obtain the release lock and verify its signature,
platform ID, x86_64 image digest, CLI/MCP package versions and integrity hashes,
license requirements and fixture revision. Install exactly those client
packages using the lock's package coordinates. No `latest`, source-built
replacement or invented version pin is a candidate install.

The local installer entry point required by the release journey is:

```powershell
honua admin install local --profile gp-dev
```

Use it only with the candidate-pinned installer and its documented lock
selection. Retain its deployment receipt, wait for readiness, and consume
the generated credential-safe MCP configuration. Confirm the generated
profile's endpoint and candidate identity. Do not copy a key out of MCP
output or create a second key through a model tool.

For AWS ECS-small, configure the pinned DevOps stdio server in the terminal:

```powershell
honua-devops --mcp
```

Discover its actual plan/apply schemas. Review the target account, region,
cost and resource plan before human approval of apply. Before configuring
Honua, require a verified serving endpoint, candidate digest, authentication,
active Admin profile, and successful proxy `tools/list` handoff. A provider
message saying `applied` without those observations is not a handoff. Rejoin
the same configuration journey below only after all five checks pass.
EKS and Azure are outside this bounded placement path.

These install/handoff commands are the required replay entry points, not a
claim that this docs lane installed the as-yet uncut candidate. For a separate
source-development setup, use [source-development quickstart](../internal/developer/source-quickstart.md);
its source build does not qualify this packaged terminal journey.

## 2. Establish least privilege and server discovery

Keep a proposer profile and a separate human approver profile backed by
different server-resolved principals. Give each the needed target permissions;
the approver's focused API-key recipe is `admin:read` plus `admin:approve`,
subject to the candidate's tenant/resource authorization. Different profile
names using the same principal do not satisfy separation of duties.
Never provide the approver profile, database password or raw key to the model.

Use the discovered Admin key-list operation and
`getAdminApiKeyEffectivePermissions` to verify the installer-created key's
ID and effective permissions. Keep secret values in the OS credential store
or private generated config. The canonical
[Admin API overview](../reference/admin-api/overview.md) describes discovery;
use the candidate's generated CLI reference for command grouping and required
arguments, rather than inventing a generic `honua_admin_*` tool.

After MCP initialization, call `honua_list_capabilities`, then request the
server-authored setup view:

```json
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"view":"setup"}}
```

Retain its revision, membership and descriptor digests, descriptor count,
byte size and paging state. The source view is `setup.v2`; a discovered
descriptor does not prove its downstream operation works. Record the
candidate's actual profile/catalog counts instead of copying historical
counts. Discover the explicit analysis and Esri GP profile surfaces when
testing GP; profile filtering and the GA process catalog are different
denominators. Missing tools are a stop condition, not permission to synthesize
a client-owned replacement catalog.

## 3. Connect, import, publish and verify access

Using the discovered Admin operation schemas, create the source connection
(`createConnection`), test it (`testConnection`), preview and import the
pinned fixture, then publish the service/layer. Supply connection secrets
through the client's protected input mechanism. Validate requested writes and
honor any returned proposal before applying changes.

Retain connection ID, import job/result, service ID and service-local layer
index. Discover the layer with `honua_list_layers`, describe its schema and
CRS, and query it using the intended consumer identity. Assert fixture feature
count, attribute values, geometry type, coordinates and CRS from the fixture's
independent expectations. Also assert denial with a consumer lacking access;
a publisher's successful read alone does not prove the access policy.

The [first dataset guide](first-dataset.md) supplies the underlying connection
and publication workflow. The packaged local and ECS candidate replay must
retain the actual calls, identities and results for this stage.

## 4. Apply the published layer's canonical style and render

Use these server-discovered tools in sequence. Substitute the actual service
ID, layer index, catalog preset ID and fixture extent; these are argument
templates, not fabricated successful tool output.

| Tool | Arguments / result to retain |
|---|---|
| `honua_get_style` | `{}` lists presets; then `{serviceId, layerId, includeStylesheet: true}` resolves the published layer's current style. |
| `honua_apply_style_preset` | `{serviceId, layerId, styleId}` selects an existing catalog preset as the layer's primary/default style. |
| `honua_get_style` | Resolve the same layer again; assert its style ID and stylesheet match the selected preset. |
| `honua_render_map` | `{layers: [{serviceId, layerId}], bbox: [minX,minY,maxX,maxY], bboxSrid: fixtureSrid, width: 512, height: 512}`, substituting the fixture's numeric SRID and extent in that CRS. Transform the extent first if requesting another output CRS. |
| Resource read | Read the returned resource URI with MCP `resources/read` when supported; fetch an HTTP artifact link through the authenticated client as directed by the returned descriptor. Retain artifact identity, media type, dimensions and style references. |

Render evidence must show the expected fixture features and selected style,
not merely a non-empty PNG. A draft-only style reference does not establish
published-layer rendering. The clean profile/catalog/style receipt belongs to
[SDK #1401](https://github.com/honua-io/honua-sdk-js/issues/1401).

## 5. Discover, execute, wait and read GP

Follow [Geoprocessing with AI](../guides/query-analyze/geoprocessing-with-ai.md):
describe `geometry.buffer`, validate the plan with `honua_validate_plan`,
then use `honua_execute_plan` only with its required Pro entitlements and
`Process.Execute` authority. Community can use the authenticated OGC process
path described there. For the Pro MCP plan, read its returned
`honua://jobs/{jobId}` until terminal, then `honua://jobs/{jobId}/results`.
For synchronous Community OGC execution, retrieve output through the SDK's
`run.results()`; there is no asynchronous job ID to fabricate. For an
asynchronous OGC request, poll its returned OGC job URL and follow its results
link. In each case retain the output artifact and CRS.

Use the explicit analysis/Esri profile discovery to check the candidate's
available adapters. Never infer a direct `buffer_features` implementation
from a profile name: the source guide still identifies #3269 as its blocker.
The one buffer example is a bounded walkthrough of the **whole-catalog GP GA
contract**, not a reduction of the release's GA denominator. All four
cloud-native formats likewise retain their own GA qualification.

For replay, independently compute expected output for the selected fixture
before executing. Assert feature/attribute values, geometry type, ordinates
and CRS; for raster work include nodata and metadata. A snapshot of current
output, a completed job status, or an artifact URL alone is not correctness
evidence. Carry the verified artifact into the Studio composition.

## 6. Author, validate, save and reopen

Confirm that the candidate advertises `setup.v2` and its canonical create,
read, update, validate, preview, save and reopen descriptors. Older candidates
may expose only part of this sequence; record the missing tools and stop that
qualification cell instead of inventing a client-side catalog.

Create a map draft with the published layer or verified GP result in its
composition envelope. Use `honua_studio_update_draft` with the returned schema
to edit layers, style references, view, widgets and controls. Pass the latest
`generation` on every mutation. A stale generation requires a fresh read and
reviewed retry. `honua_studio_validate_draft` and preview are read-only.
Granular composition tools remain available through the explicit paginated
`full` catalog escape hatch; retain its digests separately if you use it.

Call `honua_studio_save_version` with `draftId`, `generation` and an optional
`changeNote`. A completed operation returns `version`, including `itemId`,
`versionId`, `contentHash`, envelope and validation. Saving refreshes the
mutable draft generation, so read it again before editing that same draft.
After approval, poll `honua://proposals/{proposalId}` until `Succeeded` and
read `resourceIds`: save returns `itemId`, `versionId`, and `contentHash`;
reopen returns `draftId` and `generation` (as strings). These result identities
are visible to the proposer only after the canonical operation completes.

Call `honua_studio_reopen_version` with the saved `itemId` and `versionId` to
create an editable draft. Assert its actual layer, view, widget and style
values against the independent fixture, and retain its `draftId`,
`baseVersionId`, `generation` and body. **A successful get-draft is not
save/reopen proof.** Approval-required envelopes have no completed version or
reopened draft yet; follow their proposal and retain that outcome.

Repeat for a dashboard using the discovered composition/widget schemas.
If the candidate rejects dashboard composition, retain its structured failure
and the [#3429](https://github.com/honua-io/honua-server/issues/3429) disposition;
do not relabel it as a map. A source fixture does not certify the pinned
candidate: retain separate real-model and exact-candidate execution receipts.
[SDK #1397](https://github.com/honua-io/honua-sdk-js/issues/1397) and
[SDK #1398](https://github.com/honua-io/honua-sdk-js/issues/1398) own the
server-discovered routing and lifecycle client.

## 7. Govern publication and report the final URL

Save the draft as an immutable version, then call
`honua_studio_propose_publication` with its `itemId`, `versionId`, and
`contentHash` plus the requested `route` and `visibility`. The tool submits
that immutable binding to the canonical proposal runtime and returns durable
proposal, operation, and audit identities plus a `proposalUri`. It does not
save a version or move the published pointer before approval succeeds.
Do not pass a draft ID to the approval command.

Verify that the pinned candidate exposes this contract before submitting.
The source bridge is not an exact-candidate end-to-end execution receipt;
retain the save/reopen and candidate replay requirements above. Poll the
returned `proposalUri` from the proposer session. A separate authorized
principal must approve the proposal. The
human reviews the immutable version/hash, target route, visibility, risk,
diff and policy. For a real canonical proposal, the approval command is:

```powershell
honua admin operate approveOperationProposal --path "id=$proposalId" --profile approver --yes
```

The corresponding read is:

```powershell
honua admin operate getOperationProposal --path "id=$proposalId" --profile proposer
```

The proposer must be denied self-approval. Approval must preserve the original
tenant, owner and scope boundary. Poll the canonical operation to terminal
success, then read and verify the final URL under its intended visibility.
Join deployment, resource, render, job, draft/version/hash, proposal, operation,
approver, audit and final URL identities. Do not derive a URL from a naming
convention or treat recorded publication intent as success.

## Recovery and optional visual inspection

For a failed job, retain structured errors and the job/artifact IDs; inspect
before resubmitting. For a stale draft, reload and reconcile generations. For
a rejected/expired proposal, inspect the sealed decision and create a newly
reviewed proposal if needed. Review cost, destructive changes and exposure
widening at their respective human gates; approval of install does not approve
public publication. Preserve the local stack until all receipts are collected,
then use its install receipt's cleanup procedure after deciding what data to
retain.

Optional Studio/Console clients must read the same server IDs. Configure
`HONUA_CONSOLE_MODE=witness` only on a compatible pinned Console package
that supports that mode. The focused witness shows Operate reads, releases,
deploy status and proposal approval. Configuration writes go through Admin
CLI or the agent path. The `admin:read`/`admin:approve` recipe still needs
the focused candidate receipt tracked by
[#3365](https://github.com/honua-io/honua-server/issues/3365).
An optional browser walkthrough neither establishes nor blocks terminal-path
completeness. Multi-tenancy, customer alerting and offline sync remain Preview;
no hosted model is required.
