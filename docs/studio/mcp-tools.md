---
type: guide
title: "Studio MCP tools"
description: "The server publishes 20 typed Studio tools through /mcp."
resource: "honua://capability/ai.mcp-discovery"
---
# Studio MCP tools

The server publishes 20 typed Studio tools through `/mcp`.
This tool plane is executable independently of the browser Studio preview.
The `configure` workflow view includes these tools. Select that view from the
`setup_and_publish` and `dashboard_scaffolding` prompts. The older `setup` view
does not include `honua_studio_get_version`.

| Tool | Semantics |
|---|---|
| `honua_studio_create_draft` | Create a map, app, or dashboard draft. |
| `honua_studio_get_draft` | Read the current draft and generation. |
| `honua_studio_update_draft` | Replace a draft envelope at an expected generation. |
| `honua_studio_validate_draft` | Validate without mutating the draft. |
| `honua_studio_preview_draft` | Return a preview plan without publishing. |
| `honua_studio_add_layer` | Add one layer. |
| `honua_studio_remove_layer` | Remove one layer. |
| `honua_studio_set_layer_style` | Replace a layer's typed style. |
| `honua_studio_set_layer_visibility` | Set a layer's visibility. |
| `honua_studio_set_view` | Set center, zoom, bearing, and pitch. |
| `honua_studio_add_widget` | Add a widget. |
| `honua_studio_remove_widget` | Remove a widget. |
| `honua_studio_bind_interaction` | Bind a typed source event to an action. |
| `honua_studio_remove_interaction` | Remove an interaction binding. |
| `honua_studio_add_control` | Add a map control. |
| `honua_studio_remove_control` | Remove a map control. |
| `honua_studio_save_version` | Save the draft as an immutable version. `versionId` and `contentHash` stay nested under `version`. |
| `honua_studio_get_version` | Read a saved version by `itemId` and `versionId`. Returns top-level `versionId` and `contentHash`. A missing version is `not_found`. Does not advance the draft generation. |
| `honua_studio_reopen_version` | Branch a new draft from a saved version. |
| `honua_studio_propose_publication` | Propose an exact saved version for publication. The configure prompts tell an admin to finish this call in the same session. |

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
The configure prompts tell an admin to finish that call in the same session and to
read the share URL from the result. A caller who is not an admin receives a proposal
and does not get a share URL.
