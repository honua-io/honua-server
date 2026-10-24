---
type: guide
title: "Studio MCP tools"
description: "Typed Studio composition, publication and saved-version restoration through /mcp."
resource: "honua://capability/ai.mcp-discovery"
---
# Studio MCP tools

The server publishes typed Studio tools through `/mcp`.
This tool plane is executable independently of the browser Studio preview.
The `configure` view includes basic map composition; `setup` also includes full
dashboard composition. Both include publication and saved-version restoration. The
`setup_and_publish` and `dashboard_scaffolding` prompts select `configure`
because that view also carries the closed admin roster. `setup` (`setup.v8`)
is the bounded terminal path and is what stamps `_meta["honua.studio"].view`.

| Tool | Semantics |
|---|---|
| `honua_studio_create_draft` | Create a map, app, or dashboard draft. |
| `honua_studio_get_draft` | Read the current draft and generation. |
| `honua_studio_update_draft` | Replace a draft envelope at an expected generation. |
| `honua_studio_validate_draft` | Check envelope structure without mutating the draft, querying sources or rendering. |
| `honua_studio_preview_draft` | Return a plan without querying sources, rendering a canvas or publishing. |
| `honua_studio_add_layer` | Add one layer. |
| `honua_studio_remove_layer` | Remove one layer. |
| `honua_studio_set_layer_style` | Replace a layer's typed style. |
| `honua_studio_set_map_style` | Replace the map's typed style. |
| `honua_studio_set_layer_visibility` | Set a layer's visibility. |
| `honua_studio_set_view` | Set center, zoom, bearing, and pitch. |
| `honua_studio_add_widget` | Add a widget. |
| `honua_studio_remove_widget` | Remove a widget. |
| `honua_studio_bind_interaction` | Bind a typed source event to an action. |
| `honua_studio_remove_interaction` | Remove an interaction binding. |
| `honua_studio_add_control` | Add a map control. |
| `honua_studio_remove_control` | Remove a map control. |
| `honua_studio_save_version` | Save the draft as an immutable version. `versionId` and `contentHash` stay nested under `version`. |
| `honua_studio_get_version` | Read a saved version by `itemId` and `versionId`. Returns top-level `versionId` and `contentHash`, and current `pointers` when the caller may read the item. A missing version is `not_found`. Does not advance the draft generation. |
| `honua_studio_reopen_version` | Branch a new draft from a saved version. |
| `honua_studio_propose_publication` | Publish the current saved version directly for an authorized admin, or return an approval proposal for another caller. Bind the exact item, version, hash, route, and visibility. |
| `honua_studio_propose_restore` | Propose restoration of an original saved version using its hash and the observed pointer revision. Workspace agents require a separate human approval. Editable drafts remain unchanged. |

Every mutation that accepts `generation` uses optimistic concurrency. A stale
generation returns `failed_precondition` with the owner-authorized snapshot's
`currentGeneration`. Fetch the draft again and reconcile the intended mutation:
retry only when it remains valid and non-conflicting. A conflict requires explicit
resolution; the server never blindly replays a mutation. Dashboard drafts use
the same composition editor, whole-document validation, and durable lifecycle
as map/app drafts.

Publication is not a canvas mutation. Save the draft as an immutable version, then read it
back with `honua_studio_get_version` when a capture needs the top-level `versionId` and
`contentHash`. Pass the saved version's `itemId`, `versionId`, and `contentHash` together
with the requested `route` and `visibility` to `honua_studio_propose_publication`.

A workspace agent needs an exact `agent:studio-compose:<item-guid>` permission to
create, compose, save, and read that item, and
`agent:studio-propose-publication:<item-guid>` to propose its publication. The
controller allocates `itemId` before credential issuance; agents omit `ownerId`.
Send the actual `X-Honua-Workspace` and `X-Honua-Turn` on every call. Publication
requires a distinct authenticated human approval and rechecks the original agent's
live credential, workspace binding, saved version, and hash. See
[Workspace credentials](../guides/secure/authentication.md) for persisted delegation
grants and OAuth ceilings.
The configure prompts tell an admin to finish that call in the same session and to
read the share URL from the result. A caller who is not an admin receives a proposal
and does not get a share URL.

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

Realtime draft editing executes directly under the default policy in every
edition, including `Licensing:Mode=Disabled`. An explicit operator override can
require approval or block `StudioDraftMutation`; clients must respect the actual
operation outcome. Do not change deployment policy as an implicit part of a
creative edit. Publication and rollback carry separate action discriminators
and retain their governed policy; the realtime draft rule does not bypass them.

Publication has a separate branch: an authorized admin receives `Published`,
`humanConfirmationRequired: false`, and the server-issued `shareUrl` only after
completion. A non-admin receives `AwaitingApproval`,
`humanConfirmationRequired: true`, and the proposal identity with no share URL.
Follow that proposal to its decision and activation; never have the proposer
approve itself. Neither path changes the requested audience implicitly.

## Restore a saved version

Read the original version with `honua_studio_get_version`. Pass its `contentHash`
and the returned `pointers.currentVersionId` and `pointers.pointerRevision` to
`honua_studio_propose_restore`, together with `itemId`, `targetVersionId`,
`pointer` (`current`, `published` or `both`), `expectedCurrentVersionId`,
`expectedPointerRevision`, and a stable `idempotencyKey`.

The independent permission `agent:studio-restore:<item-guid>` authorizes a
workspace agent to request this operation; composition or publication permission
alone does not. Use the actual workspace and turn headers. The proposal describes
the exact original version, hash and pointer change for a distinct human to review.
Treat `humanConfirmationRequired: true` as pending and follow the canonical proposal.

Approval rechecks the original agent and turn. An intervening pointer change makes
the proposal stale, even if the pointer was subsequently changed back. Refresh the
observed state and obtain a new approval. A lost response can be retried with the
same key and identical arguments to recover the original receipt. Restoration
changes the selected pointers without creating a replacement version or overwriting
an editable draft, including edits made while approval was pending.

A browser client can support fewer result branches than the server. In particular,
the Studio Preview sharing adapter currently models the non-admin proposal path;
its fixture evidence does not establish support for the admin direct-publication
response. Use the installed client/server contracts, not a shared product name,
to determine which journey is supported.
