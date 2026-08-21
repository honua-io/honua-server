# Studio MCP tools

The `/mcp` endpoint publishes these 20 `honua_studio_*` tools. They author
drafts and immutable versions; they do not grant an agent permission to publish
or share.

| Tool | Semantics |
| --- | --- |
| `honua_studio_create_draft` | Create a typed Studio composition draft. |
| `honua_studio_get_draft` | Read a draft and its current generation. |
| `honua_studio_update_draft` | Replace draft composition with optimistic concurrency. |
| `honua_studio_validate_draft` | Validate the current draft without publishing it. |
| `honua_studio_preview_draft` | Produce the server-owned preview plan. |
| `honua_studio_save_version` | Save exactly one draft generation as an immutable version; returns `itemId` and `versionId`. |
| `honua_studio_get_version` | Read an immutable version by `itemId` and `versionId`. |
| `honua_studio_reopen_version` | Reopen an immutable version as a new draft whose `baseVersionId` identifies the source version. |
| `honua_studio_add_layer` | Add a layer to the composition. |
| `honua_studio_remove_layer` | Remove a layer. |
| `honua_studio_set_layer_style` | Set a layer's style payload. |
| `honua_studio_set_layer_visibility` | Show or hide a layer. |
| `honua_studio_set_view` | Set center, zoom, bearing, pitch, or bounds. |
| `honua_studio_add_widget` | Add a dashboard/app widget. |
| `honua_studio_remove_widget` | Remove a widget. |
| `honua_studio_bind_interaction` | Bind a typed event to an action. |
| `honua_studio_remove_interaction` | Remove an interaction binding. |
| `honua_studio_add_control` | Add a map/app control. |
| `honua_studio_remove_control` | Remove a control. |
| `honua_studio_propose_publication` | Record publication intent for later human review. |

Every mutation of an existing draft, including `honua_studio_save_version`,
takes the generation returned by the preceding read or mutation. A stale
generation returns the typed MCP error `failed_precondition`. On that error,
call `honua_studio_get_draft`, reconcile against the latest composition, and
retry once with the new generation. Never blindly replay a mutation and never
turn a proposed publication into an agent-executable publish.

Fresh deterministic map/app scratch drafts use Redis when the server has Redis
configured, so their identifiers resolve from another replica and after a
process restart. They expire according to `PackageDraftRetentionOptions`.
