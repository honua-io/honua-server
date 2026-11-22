---
type: guide
title: "Studio MCP tools"
description: "The server publishes 23 typed Studio tools through /mcp."
resource: "honua://capability/ai.mcp-discovery"
---
# Studio MCP tools

The server publishes 23 typed Studio tools through `/mcp`.
This tool plane is executable independently of the browser Studio preview.
The `setup` workflow view (`setup.v7`) includes 22 of these tools — every
Studio tool except `honua_studio_set_map_style` — and stamps
`_meta["honua.studio"].view` and `_meta["honua.studio"].revision`. The
`configure` view (`configure.v4`) carries the admin connection, import, and
publish roster and includes no Studio tools. The `dashboard_scaffolding` prompt
selects `setup`, while `setup_and_publish` starts on `configure` for admin
setup and switches to `setup` for Studio authoring.
To list every tool, including `honua_studio_set_map_style`, send an
authenticated `tools/list` request with `params.view` set to `"full"` and
repeat it with `params.cursor` set to each `nextCursor` until none is returned.

| Tool | Semantics |
|---|---|
| `honua_studio_create_draft` | Create a map, app, or dashboard draft. |
| `honua_studio_get_draft` | Read the current draft and generation. |
| `honua_studio_update_draft` | Replace a draft envelope at an expected generation. |
| `honua_studio_validate_draft` | Check envelope structure without mutating the draft, querying sources or rendering. |
| `honua_studio_preview_draft` | Return a plan without querying sources, rendering a canvas or publishing. |
| `honua_studio_add_layer` | Add one layer. |
| `honua_studio_remove_layer` | Remove one layer, with the source binding, inline style, map-style source and render layers no other layer uses. |
| `honua_studio_set_layer_style` | Bind or clear a layer's `styleRef`, or store a `styleBody` as the layer's inline style. Clearing keeps shared style definitions. |
| `honua_studio_set_layer_visibility` | Set a layer's visibility. |
| `honua_studio_set_map_style` | Replace the draft's map style with an edited MapLibre style. Sources and existing render-layer bindings cannot change. Clears style definitions and layer style bindings. |
| `honua_studio_set_view` | Update center, zoom, bearing, and pitch. Omitted members keep their values. Zoom outside [0, 24], pitch outside [0, 85] or bearing outside [0, 360) is refused with `out-of-bounds`. |
| `honua_studio_add_widget` | Add a widget. |
| `honua_studio_remove_widget` | Remove a widget. |
| `honua_studio_bind_interaction` | Bind a typed source event to an action. |
| `honua_studio_remove_interaction` | Remove an interaction binding. |
| `honua_studio_add_control` | Add a map control. |
| `honua_studio_remove_control` | Remove a map control. |
| `honua_studio_list_items` | List saved maps and other Studio items in the caller's authorized owner and tenant scope (`items[].itemId`, `currentVersionId`, `nextCursor`). |
| `honua_studio_list_versions` | List authorized immutable versions of an item, newest first, including `versionId` and `contentHash`, with `beforeVersionNumber` / `nextBeforeVersionNumber` pagination. Does not advance the draft generation. |
| `honua_studio_save_version` | Save the draft as an immutable version. `versionId` and `contentHash` stay nested under `version`. |
| `honua_studio_get_version` | Read a saved version by `itemId` and `versionId`. Returns top-level `versionId` and `contentHash`. A missing version is `not_found`. Does not advance the draft generation. |
| `honua_studio_reopen_version` | Branch a new draft from a saved version. |
| `honua_studio_propose_publication` | Return an approval proposal for the current saved version; every MCP caller, administrators included, waits for approval in Console. Bind the exact item, version, hash, route (a path starting with `/`), and visibility. A retry with the same arguments, or the same optional `idempotencyKey`, returns the original proposal and its current status instead of filing another. |

Every mutation that accepts `generation` uses optimistic concurrency. A stale
generation returns `failed_precondition` with the owner-authorized snapshot's
`currentGeneration`. Fetch the draft again and reconcile the intended mutation:
retry only when it remains valid and non-conflicting. A conflict requires explicit
resolution; the server never blindly replays a mutation. Dashboard drafts use
the same composition editor, whole-document validation, and durable lifecycle
as map/app drafts.

When draft creation requires approval, retain the returned proposal identity and
wait for a person to approve it. The original proposer can read
`honua://proposals/{proposalId}` after execution succeeds to recover
`resourceIds.draftId` and `resourceIds.itemId` from the completed operation,
including after a server restart. Read that draft with `honua_studio_get_draft`
for its current generation before editing. Pending proposals do not expose these
identities. If the completed result has no draft identity, report the missing
result; do not issue a second create request to recover it.

Publication is not a canvas mutation. Save the draft as an immutable version, then read it
back with `honua_studio_get_version` when a capture needs the top-level `versionId` and
`contentHash`. Pass the saved version's `itemId`, `versionId`, and `contentHash` together
with the requested `route` and `visibility` to `honua_studio_propose_publication`.
Every caller receives a proposal and no share URL; the route is served once the
proposal is approved in Console (see
[Approve agent changes](../guides/secure/approve-agent-changes.md)). Calling again
with the same arguments (or the same optional `idempotencyKey`) reports the original
proposal's status instead of filing another: `AwaitingApproval`, `Published`,
`Rejected` or `Denied`. Send a new `idempotencyKey` to propose again after a
rejection. Without a key, a proposal that failed, was cancelled or expired is
replaced by a fresh one; with your own key the call says to send a new key. Reusing
a key for different arguments is refused.

## Layer identity and observed rendering

Discover readable published layers through `honua_list_layers` or `honua_describe_layer`.
Copy the returned `studioSourceId` into a catalog-backed composition layer's `sourceId`.
Choose a separate `layer.id` for that drawing element. The source identity follows the
existing Studio catalog contract; it grants no data or publication permission.
Catalog-backed editing does not require portable `sourceBindings` or a `mapSpec`
export. Supported portable bindings and `metadata.honua.source` overrides remain
separate mechanisms with their existing contracts.

Validation reports structure. Preview reports a plan for every package family.
A successful result from either tool leaves source resolution, current data access
and rendering unverified. `honua_render_map` can produce an image of the requested
published data; that image does not verify a saved Studio composition. Open that
exact composition in the actual Studio canvas or a supported consumer before
claiming its layers, styles and viewport rendered correctly. Saving and publication
also require their own receipts and audience checks.

## Composition policy

Every MCP caller is an agent, administrators included. Draft mutations can return
`RequiresApproval` with a proposal identity instead of a draft or version result.
A person approves or rejects that proposal in Console; an agent credential cannot
decide it. Respect the actual operation outcome and retain the original proposal
while awaiting approval. Do not change deployment policy or add requester
permissions as an implicit part of a creative edit.

Publication always waits for a person. Every MCP caller is an agent,
administrators included, so a new proposal returns `AwaitingApproval`,
`humanConfirmationRequired: true`, and the proposal identity with no share URL.
A person approves or rejects the proposal in Console; an agent credential cannot
decide it. The publication does not change the requested audience implicitly.
