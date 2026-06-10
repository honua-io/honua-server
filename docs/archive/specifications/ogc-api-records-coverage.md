# OGC API Records Coverage

Honua exposes a first read-only OGC API Records surface at `/ogc/records`.
The initial slice is intended for standards-based catalog discovery while the
server contract stabilizes.

## Implemented Endpoints

- `GET /ogc/records` returns the Records landing page.
- `GET /ogc/records/conformance` advertises only the implemented read-only
  Records core and GeoJSON record classes.
- `GET /ogc/records/collections` lists the single `honua-catalog` record
  collection.
- `GET /ogc/records/collections/honua-catalog` returns collection metadata.
- `GET /ogc/records/collections/honua-catalog/items` returns GeoJSON records.
- `GET /ogc/records/collections/honua-catalog/items/{recordId}` returns one
  record by stable id.

## Record Sources

The first slice projects records from published Honua catalog services and
layers that are visible to the caller. Layer records use stable ids of the form
`layer:{layerId}`. Service records use `service:{serviceName}`.

Layer records include links to matching OGC API Features, STAC, and
FeatureServer resources where those protocol surfaces are addressable. Service
records include links to GeoServices FeatureServer and MapServer surfaces.

## Query Parameters

`/items` supports deterministic in-memory filtering over the projected records:

- `limit` and `offset` page records; `limit` is capped at `1000`.
- `ids` filters by comma-separated record ids.
- `type` filters by the record `properties.type` value (`service` or
  `dataset` in this slice).
- `externalIds` filters against stable source identifiers such as layer id,
  layer name, or service name.
- `q` performs case-insensitive all-term matching over id, title, and
  description text.
- `bbox` intersects record extents when an extent is known.
- `datetime` filters record metadata timestamps when present; catalog records
  without timestamps are excluded when the filter is supplied.

## Relationship To Other Catalog Surfaces

OGC API Records describes metadata records about Honua resources. It does not
replace OGC API Features, which exposes feature collections and feature items.
It also does not replace STAC, which remains the asset/catalog shape for
spatiotemporal collections and items. Admin metadata APIs remain the operator
control-plane surface for creating, editing, and approving metadata resources.

## Non-Goals For This Slice

The Records endpoint is read-only. It does not implement record create/update,
delete, harvest, facets, advanced CQL filtering, or external catalog harvesting.
SDK client support should be added after this server contract has settled.
