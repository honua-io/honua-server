---
type: reference
title: "Styles"
description: "Reference for managing layer styles."
resource: "honua://capability/styling.defaults"
---
# Styles

Reference for managing layer styles. The canonical style document is MapLibre Style Spec v8 JSON; the server back-generates a GeoServices `drawingInfo` snapshot from it so MapServer and FeatureServer renderers stay in sync.

All admin endpoints require admin authentication — see [Authentication](../../guides/secure/authentication.md).

## Layer style read and write

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/metadata/layers/{layerId}/style` | Get the layer style (MapLibre + cached `drawingInfo` + revision metadata) |
| PUT | `/api/v1/admin/metadata/layers/{layerId}/style` | Update the layer style; accepts optional `changedBy` and `changeSummary`, reports `unsupportedSymbolizers[]` with stable codes |

The MapLibre payload must be a valid Style Spec v8 document with at least one layer; an empty `layers` array returns `400`. Layers that omit `source` default to the auto-injected tile source `layer-{layerId}` (source-layer `layer`). `changeSummary` is capped at 1000 characters. Unsupported symbolizer codes are `RENDERER_TYPE_UNSUPPORTED`, `SYMBOL_TYPE_UNSUPPORTED`, `PICTURE_MARKER_PARTIAL`, and `RENDERER_PAYLOAD_INCOMPLETE`; the request still succeeds with a best-effort fallback.

In the authorized [API explorer](../openapi-and-explorer.md), run `PUT /api/v1/admin/metadata/layers/{layerId}/style` with this body:

```json
{
  "mapLibreStyle": {
    "version": 8,
    "sources": {},
    "layers": [
      {
        "id": "parcels-fill",
        "type": "fill",
        "paint": { "fill-color": "#2D69A5", "fill-opacity": 0.4 }
      }
    ]
  },
  "changeSummary": "Initial parcels style"
}
```

## SLD import and export

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/metadata/layers/{layerId}/style/import-sld` | Convert an SLD/SE 1.0 or 1.1 XML document to MapLibre style JSON and store it (1 MiB body cap) |
| GET | `/api/v1/admin/metadata/layers/{layerId}/style/export-sld` | Export the stored MapLibre style as an `application/xml` SLD 1.0 document; diagnostic count in `X-Sld-Diagnostic-Count` |

Run `POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld` and use `parcels.sld` as the XML request body.

## Style suggestions

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/metadata/layers/{layerId}/suggest-style` | Generate a suggested style for a layer from its schema and data |

Run `POST /api/v1/admin/metadata/layers/{layerId}/suggest-style` with `{}`.

## Public style fetch and themes

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/styles/{layerId}.json` | Public MapLibre style fetch; optional `?theme=default\|dark\|colorblind-safe\|print` applies a deterministic theme transform |

The output cache varies per theme; the admin update endpoint invalidates every variant on each revision.

## OGC API Styles

Named styles are also exposed through the standards-based OGC API Styles surface:

| Method | Path | Purpose |
|---|---|---|
| GET | `/ogc/styles` | List styles |
| GET | `/ogc/styles/{styleId}` | Fetch a style |
| GET | `/ogc/styles/{styleId}/metadata` | Fetch style metadata |
| POST | `/ogc/styles` | Create a style |
| PUT | `/ogc/styles/{styleId}` | Replace a style |
| DELETE | `/ogc/styles/{styleId}` | Delete a style |

## Related guides

- [Style maps](../../guides/style/style-maps.md) — how MVT, MapServer, and WMS consume the canonical style document
- [Import SLD styles](../../guides/style/import-sld-styles.md)

### Conditional shared edits and reviewed restoration (preview)

Shared catalog edits affect live references to that catalog style. A saved map that
retains a shared style reference can therefore change appearance when the shared
style changes; its immutable map version is not an immutable copy of the referenced
style bytes. A map-local full-style snapshot detaches those references and retains
its own appearance. The editor must disclose this choice explicitly.

Read the stylesheet ETag and `/ogc/styles/{styleId}/impact` before editing. Conditional
PUT requires the exact `If-Match: "style-vN"` and `X-Honua-Style-Impact` hash from the
reviewed revision. Stale revisions or changed impact return 412 without overwriting.
Impact separates direct data-layer/service `associations` from versioned map/app
`consumers`, including drafts, loadable saved versions and current published pointers.
Only unresolved `styleRefs` are live dependencies; inline bodies are detached.
The index derives references from canonical package bodies/bindings and resolves
actual identity through workspace/family/tenant joins. Declared dependency lists
are not authority. An exclusive transaction barrier keeps reference creation and
pointer changes from racing a reviewed shared edit; ordinary Studio writers share
the barrier and do not acquire human approval for creative edits. `impactComplete`
and `canEdit` must both be true before saving. Hidden consumer IDs and counts are
omitted; incomplete, unresolved, oversized or unauthorized scope disables editing. Existing headerless human clients remain
compatible, so conditional editing must be selected explicitly by new clients.

Workspace agents read a frozen `shared-style` task with
`honua_shared_style_get` and propose a complete replacement with
`honua_shared_style_propose`. Proposal preparation and execution require the live
`ai.approval-workflows` entitlement (Pro or Enterprise).
Actual current credentials, membership, tenant,
revision, bytes, and affected resource authority are checked again on execution.
The agent cannot approve its own proposal or bypass this through direct style PUT.
Map-local draft composition remains a separate realtime edit.

Canonical approved shared-style operations retain before and proposed bytes in the
sealed server-side plan. To restore a retained prior appearance, attach a fresh task
for the current style revision and submit `restoreFromProposalId` instead of an
`intent` to `honua_shared_style_propose`. This creates a new human review proposal
and advances the style revision on approval. It does not decrement a version or
infer old appearance from a version number. Expired, missing, unverified, or no
longer authorized receipts cannot restore automatically. The current style table
alone is not a revision archive. Validation and source tests do not establish
rendering or browser qualification.

Governed edits also write an immutable SQL execution receipt in the same transaction
as the style bytes. The receipt binds the canonical operation/proposal, sealed request,
original actor, tenant, schema, target, reviewed stored/effective content and result.
It contains no credentials or style documents. Receipt retention is currently indefinite;
automatic recovery still requires the retained, verified canonical proposal and its
original durable human approval claim. Legacy executing proposals without that claim
require manual investigation. An authenticated original requester polling
`honua://proposals/{proposalId}` can adopt a matching committed receipt into the existing
operation and proposal without actuating another edit or obtaining another approval.
Recovery rechecks current key, roles, membership, tenant and readable scope, preserves
terminal operation results, and refreshes projections from current canonical storage.
A missing receipt never proves success and cannot advance canonical state.

Lost shared-style responses can be reconciled using the returned operation identity:
`honua_workspace_get_command` accepts `workspaceId` and exactly one of `commandId`
or `operationInstanceId`. Operation IDs support sealed shared-style and workflow
publication proposals. A later chat may read the original operation; the server checks
its original workspace, task, target, tenant, credential and human approval rather
than using the later chat's selection. An exact committed SQL receipt completes
uncertain canonical status without another style write or approval. Missing
receipts never establish success. Repeating the original proposal with the same
frozen turn and payload also returns that same operation; it cannot retarget a
new selection or restore old bytes.
