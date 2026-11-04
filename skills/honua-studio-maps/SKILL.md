---
name: honua-studio-maps
description: Use when composing a saved map in Honua Studio in Console, including layer styles, labels, filters and team or organization publication.
---

# Compose and save a Studio map

Read the install's `AGENTS.md`. Open Studio in Console for interactive editing,
or discover the Studio MCP tools for the same draft lifecycle. Discover the
catalog layers with `honua_list_layers` and `honua_describe_layer` first.

1. Create a map draft with `honua_studio_create_draft`, or reopen a selected saved
   version with `honua_studio_reopen_version`. Retain the returned draft id and
   generation. Read the draft with `honua_studio_get_draft` before editing.
2. Add the intended layers with `honua_studio_add_layer`. Copy `studioSourceId`
   from catalog discovery into `sourceId`; a composition layer's `id` is a
   different identity. Set a useful map extent with `honua_studio_set_view`.
3. Set the renderer, labels and filter in Studio's layer/style editor. Persist
   the style before binding it with `honua_studio_set_layer_style` and its
   `styleRef`. See [saved style and version](references/save-share.md).
4. Pass the current generation on every mutation. After a generation conflict,
   re-read the draft and reconcile the intended edit; do not overwrite concurrent
   edits with a stale generation. Validate with `honua_studio_validate_draft`
   and preview with `honua_studio_preview_draft`.
5. Save with `honua_studio_save_version`. Retain the returned `itemId`,
   `versionId` and `contentHash`. If approval is required, wait for completion;
   there is no saved version until the operation succeeds. Verify with
   `honua_studio_get_version` and render the saved map in Console.
6. Share only the saved version and requested audience. Use
   `honua_studio_propose_publication` with the exact item, version, hash, route
   and visibility, following the server's schema. A separate approver handles
   any required approval. Verify the final published version and audience.

Map sharing and source service permissions are separate. Do not make services
public to make a team map render. If the intended audience cannot access a
layer, explain the gap and ask before widening that layer's access.
