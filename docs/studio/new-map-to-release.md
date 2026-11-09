---
type: guide
title: "How to take a Studio map from New map to release"
description: "Create a private draft, save an immutable version, review publication and verify the viewer result."
---
# How to take a Studio map from New map to release

Use Console's embedded Studio to create a server draft, build a map, save a
version and release that exact version to its intended audience. Studio remains
[Preview](README.md); a draft, saved version, pending publication and published
map are separate states.

**Availability:** this journey requires the coordinated Console/server candidate
with direct human draft creation and the human approval channel. Earlier
candidates can leave **New map** awaiting approval instead of opening a draft.
If that happens, stop and upgrade to a candidate qualified for this journey;
do not weaken the deployment's policy or call the empty canvas a created map.

## Create and compose

You need an interactive Console sign-in, a connected environment, permission
to author maps, and a readable published vector layer. Conversational editing
also needs the configured model connection. The current catalog-backed map
rendering supports OGC API Features and GeoServices FeatureServer vectors;
other source types can report that they cannot be rendered.

1. Open **Studio** (`/studio`). Check the connected environment, enter a unique
   **New map package key**, such as `transport-map`, and choose **New map**.
   Wait for **Shared server draft opened.** before editing.
2. Add a readable published layer with the layer picker. Check that its features
   appear in the canvas at the expected location.
3. Refine the map with its editing controls or a concrete prompt, such as
   “Style the roads by class and show a legend.” Review the visible result and
   activity; an assistant's completion sentence alone does not prove a change.

A signed-in human's ordinary private draft edits do not require a second
approval proposal. Agent-proposed consequential changes follow their actual
server approval outcome; do not turn a pending result into a completed edit.

## Save and reopen a version

1. Choose **Save version** and wait for the saved version number.
2. Open **Saved versions** and identify the version you intend
   to release. A version is an immutable snapshot; later draft edits do not
   update it.
3. To resume that snapshot, choose **Reopen as draft**. Check the opened map
   before further edits. Preserve unsaved work before replacing the current
   draft.

Standalone Studio's lifecycle panel uses **Validate** and **Save as version**
for the equivalent lifecycle actions. Follow the labels in your installed
client rather than assuming that every host has identical controls.

## Review and release

1. Choose **Review publication** on the saved version to publish. Check its
   version and content hash, then review the **Publication route** and
   **Who can see this map?** audience,
   including who can read every layer dependency. Resolve validation errors and
   explicitly acknowledge any warning the client requires.
2. Choose **Publish reviewed version** once. Keep the
   returned operation and proposal identities. If a confirmation asks for the
   package key, type the key of this map after reviewing the version.
3. Follow the actual result. A completed publication can return its viewer
   link immediately; an awaiting-approval result remains pending. In that case,
   follow **Open approval** and use the
   [interactive human approval flow](../guides/secure/approve-agent-changes.md).
   The requesting person can approve in the updated one-human model.
4. Return to the original publication and choose **Refresh publication status**.
   If the response is lost, use **Retry original submission** when offered;
   this retains the original command rather than creating another publication.

The agent's publication suggestion, a successful save, and an approval decision
are each distinct from a completed release. Only report the map as published
after the operation completes and the intended viewer can open it.

## Verify and recover

Open the server-issued viewer link using the intended viewer account. Check the
map, its selected version and its layer data. For a public publication, also
test an anonymous viewer. A public map document does not grant access to private
layers; a private map does not revoke an independently public service.

- **New map reports a pending action:** the candidate may lack the direct human
  draft repair. Retain the operation reference and have the operator check the
  installed candidate; do not repeatedly create the same map.
- **A layer cannot be rendered:** check source type and read permission. A
  catalog entry does not guarantee render support.
- **Save or publish reports a newer revision:** refresh and review the draft or
  version again; do not overwrite another edit silently.
- **Publication is pending or failed:** inspect the original operation and
  approval status. Do not treat a stale link or assistant message as success.
- **The released version is wrong:** use the installed governed rollback or
  unpublish action, review its exact target, and verify access again. It cannot
  recall content already downloaded by viewers.

Related: [saved-map workflow](../guides/workflows/save-resume-map.md),
[publication contract](../guides/workflows/publish-share-map.md), and
[Studio MCP tools](mcp-tools.md).
