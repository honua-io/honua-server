---
type: reference
title: "Discovery metadata"
description: "Reference for editing service and layer discovery metadata, reading its edit history, and defining custom metadata fields."
resource: "honua://capability/admin.control-plane"
---
# Discovery metadata

Discovery metadata is the title, description, keywords and custom fields that catalogs and capabilities documents show for a service or a layer. Honua stores one copy in PostgreSQL and every protocol reads it from there, so an edit here changes the GeoServices directory, OGC API collections, OGC API Records, STAC, WMS/WFS capabilities and the Portal search results together. See [How published data becomes findable](../../guides/catalog/catalog-and-discovery.md).

All routes require admin authentication. See [Authentication](../../guides/secure/authentication.md).

## Read and edit

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/services/{serviceName}/discovery` | Read a service's discovery metadata |
| PUT | `/api/v1/admin/services/{serviceName}/discovery` | Replace a service's title, description, keywords and custom fields |
| GET | `/api/v1/admin/metadata/layers/{storageLayerId}/discovery` | Read a layer's discovery metadata |
| PUT | `/api/v1/admin/metadata/layers/{storageLayerId}/discovery` | Replace a layer's title, description, keywords and custom fields |

A read returns `etag`, `entityId`, `title`, `description`, `keywords`, `customFields`, the `customFieldSchemas` that apply to the item, `affectedPublications` (every publication whose catalog entries change when you save), and the ten most recent `recentHistory` entries.

A write must send the `etag` from the read. If anything in the metadata changed since that read, the write returns `409` with "Metadata changed. Reload before saving."; read again and reapply your edit. Other limits, each answered with `400`:

- `etag` is required.
- `title` is at most 512 characters and `description` at most 16,384. Neither may contain control characters other than tab, carriage return and line feed.
- `keywords` holds at most 100 nonempty keywords of at most 256 characters each.
- Every key in `customFields` must be declared by a custom field schema that applies to the item, and its value must match that schema.

A write replaces the item's `title`, `description` and `keywords`: a field you leave out is cleared, so send back every value from the read that you want to keep. `customFields` is different: leave it out to keep the stored values, or send `{}` to clear them. Twelve writes sent at once with the same `etag` save exactly one; the others get `409`.

An unknown service name or layer ID returns `404`.

In the authorized [API explorer](../openapi-and-explorer.md), run `GET /api/v1/admin/services/parcels/discovery`, then `PUT /api/v1/admin/services/parcels/discovery` with the `etag` it returned:

```json
{
  "etag": "<etag from the read>",
  "title": "County parcels",
  "description": "Tax parcels, updated nightly.",
  "keywords": ["parcels", "cadastre"]
}
```

People edit; agents propose. A write from an AI agent or from a stored API key returns `403` with title "Approval required" and policy `operator.agent.layer-metadata`. To propose the change instead, submit it to `POST /api/v1/operations/admin.metadata.service-discovery.set/submit` or `.../admin.metadata.layer-discovery.set/submit` (or the matching MCP tool). That files a proposal, and the change is applied when a person approves it in Console Approvals.

## Edit history

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/metadata/history` | Every metadata field change, newest first; filter with `itemKind` and `itemId` |
| GET | `/api/v1/admin/services/{serviceName}/history` | Changes to one service |
| GET | `/api/v1/admin/metadata/layers/{storageLayerId}/history` | Changes to one layer |

Each entry has `id`, `revision`, `itemKind`, `itemId`, `fieldPath`, `oldValue`, `newValue`, `actor` and `changedAt`. Query parameters:

- `limit`: page size, default 50, at most 200. Zero or a negative value returns `400`.
- `beforeId`: return entries older than this `id`. Pass the previous page's `nextBeforeId` while `hasMore` is true.
- `field`: keep only entries whose field path starts with this name, for example `title` or `customFields`.

## Custom field schemas

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/metadata/schemas` | Read the custom field schemas and their `etag` |
| PUT | `/api/v1/admin/metadata/schemas` | Replace the whole list of schemas |

A schema declares one custom field: `name` (starts with a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 64 characters), optional `title` (at most 256 characters) and `description` (at most 2,048), `type` (`string`, `number`, `boolean`, `date`, `enum` or `stringList`), `required`, `allowedValues` (required for `enum`, at most 200), `appliesTo` (item kinds; empty means every kind) and `facetable` (default true). At most 64 schemas exist per environment.

The layer discovery routes edit the layer's dataset, so a field meant for them uses `appliesTo: ["dataset"]`; service routes use `"service"`. A schema that applies only to `"layer"` does not appear in the layer discovery read and cannot be written there. The write sends the read's `etag` and the complete `schemas` array; an empty array removes every schema, and a body without `schemas` returns `400`. A stale `etag` returns `409`, an invalid schema `400`. Removing a schema leaves values already stored on items in place.

For example, run `PUT /api/v1/admin/metadata/schemas` in the API explorer with:

```json
{
  "etag": "<etag from the read>",
  "schemas": [
    {
      "name": "owner_team",
      "title": "Owner team",
      "type": "enum",
      "allowedValues": ["gis", "operations"],
      "appliesTo": ["dataset", "service"]
    }
  ]
}
```

After that, a layer or service write may send `"customFields":{"owner_team":"gis"}`. A value outside `allowedValues` or of the wrong type returns `400`.
