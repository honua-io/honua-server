# Public published catalog

The public catalog reads the current metadata graph. It has no content authoring or synchronization API. Publishing changes the graph; subsequent catalog reads reflect that graph revision. Authored Console content remains a separate store.

The listing and item-detail routes are registered under the version 1 catalog API. Both accept anonymous callers and return `Cache-Control: no-store`. Authorization uses the same resource, service, tenant and credential policy checks as the Portal projection. The `access` field retains the Portal values `public`, `org` and `private`.

## Items

Each item has an `id`, `type`, `title`, `description`, `owner`, `access`, creation and update timestamps, tags, and optional folder, extent and temporal extent. Optional missing metadata remains null. Owner falls back to `honua`, and the update timestamp falls back to the creation timestamp. Extents use longitude/latitude in CRS84; projected source bounds pass through the shared coordinate transformation service.

A service item lists its visible layer IDs and protocol endpoint links. Layer rows represent each resource on a service once, preferring its primary visible publication. A folder appears only when it contains a service the caller can discover. Folder identity comes from the service's folder ID; a different folder with the same name contributes no metadata.

Service and layer URLs use the shared Portal projection and are absent when the primary protocol is unavailable to the caller. Protocol endpoint links come from `protocolEndpoints` in the caller/tenant-scoped public capability manifest, restricted by the service's enabled protocols and applicable publications. The manifest omits descriptors for excluded compiled modules, unavailable gated capabilities and disabled deployment-profile routes. Its templates use `{servicePath}`, `{serviceId}` or `{collectionId}`; collection links use each protocol's shared advertised identity resolver. Protocol-specific publication representation and storage eligibility use the same requirements as the named route.

## Filtering and paging

| Parameter | Meaning |
| --- | --- |
| `q` | Case-insensitive substring of title, description, ID or tags |
| `type` | Item type: `folder`, `service` or `layer` |
| `protocol` | Protocol identifier from a visible manifest-derived endpoint link |
| `folder` | Folder path/name |
| `tag` | Tag or keyword |
| `owner` | Published owner |
| `bbox` | Four finite CRS84 coordinates: west, south, east, north; supports crossing the antimeridian |
| `datetime` | Instant or ISO 8601 interval; `..` represents an open bound |
| `offset` | Nonnegative item offset; defaults to zero |
| `limit` | Page size from 1 to 200; defaults to 50 |

Filters intersect. Spatial and temporal filters exclude items without the corresponding extent. Results sort by title, then type and ID. The response includes `items`, `total`, `offset`, `limit` and `facets`.

Facet keys are `type`, `protocol`, `folder`, `tag` and `owner`. Each bucket contains a `value` and item `count`. Counts use the authorized, advertised population after every active filter and before paging. Repeated tags or endpoint links count an item once per facet value. Restricted or hidden publications contribute no layer, service-only-through-that-layer, folder, facet bucket, count, extent or endpoint link. An unavailable or undiscoverable detail identity returns not found.

## Advertising

Publication `advertised` defaults to true when omitted. Setting it to false removes the publication from catalog and protocol discovery. Named data requests continue to use routability and authorization. Retired or disabled publications fail named requests.

