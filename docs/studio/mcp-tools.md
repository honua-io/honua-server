# Studio MCP tools

The pinned server candidate publishes 17 typed Studio tools through `/mcp`.
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
| `honua_studio_propose_publication` | Record publication intent for governance. |

Every mutation that accepts `generation` uses optimistic concurrency. A stale
generation returns `failed_precondition`; fetch the draft, reconcile against
its new generation, and retry once. Do not loop blindly. Publication is not a
canvas mutation, and the end-to-end approval/public URL journey remains
blocked by [#3304](https://github.com/honua-io/honua-server/issues/3304).
