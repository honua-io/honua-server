# Studio MCP tools

The server publishes 17 typed Studio tools through `/mcp`.
This tool plane is executable independently of the browser Studio preview.

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
| `honua_studio_propose_publication` | Propose an exact saved version for governed publication. |

Every mutation that accepts `generation` uses optimistic concurrency. A stale
generation returns `failed_precondition` with the owner-authorized snapshot's
`currentGeneration`. Fetch the draft again and reconcile the intended mutation:
retry only when it remains valid and non-conflicting. A conflict requires explicit
resolution; the server never blindly replays a mutation. Dashboard drafts use
the same composition editor, whole-document validation, and durable lifecycle
as map/app drafts.

Publication is not a canvas mutation. Save the draft as an immutable version, then pass that
version's `itemId`, `versionId`, and `contentHash` together with the requested
`route` and `visibility`. The tool creates a durable canonical proposal and
returns its proposal, operation, and audit identities. A separate authorized
principal must approve it; poll the returned `proposalUri` for the final status
and active URL.
