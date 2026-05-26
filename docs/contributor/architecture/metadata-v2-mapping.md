# Metadata v2 ↔ Standard Mapping Specification

Formal field-by-field correspondence between V2 canonical slots and every API /
package format honua-server projects into. This document is the **authoritative
source** for cluster `Map*ResponseV2` builders: when a builder needs to emit
field X in protocol Y, the row for X in this document defines exactly which V2
slot is consulted, with what fallback chain, and what derivation is permitted
at render time.

Companion to:
- [ADR-0040](../adr/0040-metadata-v2-canonical-graph.md) — design rationale
- [metadata-v2-crosswalk.md](metadata-v2-crosswalk.md) — concept-by-concept
  inventory and gap analysis
- [metadata-v2-extensions.md](metadata-v2-extensions.md) — Extensions
  vocabulary specification

## Document conventions

Each mapping row uses the form:

```
Layer:        L1 / L2 / L3
V2 source:    <canonical slot path>
Fallback:     <fallback chain when primary slot empty>
Standards:    <protocol/format>: <field path on the wire>
              ...
Derivation:   <render-time transformation, if not direct read>
Status:       ✅ stable / ⚠️ in-flight slice / ❌ gap (open issue)
```

The **Layer** indicates where the projection logic lives (L1=read typed slot;
L2=read namespaced Extensions key; L3=compute at render time). A row marked L3
with a non-empty `V2 source` indicates the derivation chain anchors on a V2
slot but the final rendered value is computed.

## Sections

1. Identity & routing
2. Description & discovery
3. Provenance & versioning
4. Licensing & contact
5. Spatial reference & extent
6. Temporal extent & time fields
7. Schema fields (per-attribute)
8. Display / render hints
9. Filtering
10. Relationships
11. Storage binding
12. Styling
13. Capabilities / supported operations
14. Editing tracking
15. Service settings
16. Links
17. Tiling-specific
18. Raster / coverage-specific

---

## 1. Identity & routing

### 1.1 Resource id

```
Layer:        L1
V2 source:    Resource.Metadata.Id
Fallback:     (required — non-empty by graph validation)
Standards:    STAC.collection.id                  ← Resource.Metadata.Id
              OGC-API-Records.record.id           ← Resource.Metadata.Id
              OGC-API-Features.collection.id      ← Publication.Identifier.Value
                                                      (when non-numeric) ELSE
                                                    Resource.Metadata.Name
                                                      (when set) ELSE
                                                    Resource.Metadata.Id
              GeoPackage.gpkg_contents.identifier ← Resource.Metadata.Id
              FlatGeobuf.header.name              ← Resource.Metadata.Name
              GeoParquet.geo.name                 ← Resource.Metadata.Name
Status:       ✅ stable
```

### 1.2 Publication identifier (protocol-facing route key)

```
Layer:        L1
V2 source:    Publication.Identifier (Value / IsNumeric / PathOverride)
Fallback:     (required — non-empty by graph validation when Publication
              participates in service routing)
Standards:    Esri-FeatureServer.layer.id         ← Publication.Identifier.Value (parsed int when IsNumeric)
              Esri-MapServer.layer.id             ← same
              WMS.<Name>                          ← OgcClassicRequestHelpers.GetWmsLayerName(resource, publication)
              WMTS.<ows:Identifier>               ← Publication.Identifier.Value
              WFS-2.<wfs:Name>                    ← namespaced (prefix:Resource.Metadata.Name)
              WCS-2.<wcs:CoverageId>              ← Publication.Identifier.Value
              OGC-API-Records.record.id           ← Publication.Identifier.Value (when non-numeric)
              STAC.collection.id                  ← Resource.Metadata.Id (publication id ignored)
Derivation:   - Numeric routing: emit Publication.Identifier.Value as string
              - Path overrides: route mapping uses Publication.Identifier.PathOverride
                when set
              - WMS namespace prefix stripping via OgcClassicRequestHelpers.GetWmsLayerName
Status:       ✅ stable
```

### 1.3 Title

```
Layer:        L1
V2 source:    Resource.Metadata.Title (preferred) → Resource.Metadata.Name
Fallback:     Publication.TitleOverride at projection layer when set
Standards:    OGC-API-*.collection.title          ← Title ?? Name
              STAC.collection.title               ← Title ?? Name
              WMS.<Title>                         ← Title ?? Name
              WMTS.<ows:Title>                    ← Title ?? Name
              WFS-2.<wfs:Title>                   ← Title ?? Name
              WCS-2.<ows:Title>                   ← Title ?? Name
              Esri-FeatureServer.name             ← Name (not Title — Esri uses identifier-shaped name)
              Esri-FeatureServer.alias            ← Title ?? Name (in field aliases context)
              OData.<EntityType.@odata.title>     ← Title ?? Name annotation
Derivation:   Per-publication: Publication.TitleOverride wins when projecting to
              that publication's service (Esri MapServer "Service Name" vs OGC
              "collection title" can differ).
Status:       ✅ stable
```

### 1.4 Service name + route

```
Layer:        L1
V2 source:    Service.Metadata.Name + Service.Route
Fallback:     Service.Metadata.Id when Name empty
Standards:    Esri-FeatureServer service root    ← /rest/services/{group}/{name}/FeatureServer
              OGC-API-Common landingPage          ← Service.Route
              WMS service URL                     ← Service.Route + ?service=WMS
              STAC API root                       ← Service.Route
Derivation:   - Esri folder grouping: from Service.Metadata.Name dotted prefix
                ("PublicWorks.Roads" → /rest/services/PublicWorks/Roads/...)
                or from future Service.Group slot
              - Protocol suffix appended per Service.Protocols entry
Status:       ✅ stable (folder convention to be revisited if Service.Group lands)
```

---

## 2. Description & discovery

### 2.1 Description

```
Layer:        L1
V2 source:    Resource.Metadata.Description
Fallback:     empty string (most clients tolerate)
Standards:    OGC-API-*.collection.description   ← Description
              STAC.collection.description        ← Description (REQUIRED by spec)
              OGC-API-Records.record.description ← Description
              WMS.<Abstract>                     ← Description
              WMTS.<ows:Abstract>                ← Description
              WFS-2.<wfs:Abstract>               ← Description
              WCS-2.<ows:Abstract>               ← Description
              Esri-FeatureServer.description     ← Description
              Esri-FeatureServer.serviceDescription ← Service.Metadata.Description
              OData.<Summary>                    ← Description
              GeoPackage.gpkg_contents.description ← Description
              FlatGeobuf.header.description      ← Description
Status:       ✅ stable
```

### 2.2 Keywords

```
Layer:        L1
V2 source:    Resource.Metadata.Keywords[]
Fallback:     Resource.Metadata.Labels.Keys (legacy STAC port behavior; deprecated)
Standards:    OGC-API-Records.record.keywords    ← Keywords
              OGC-API-Features.collection (no required field; informational link)
              STAC.collection.keywords           ← Keywords
              WMS.<KeywordList><Keyword>...      ← Keywords (one element each)
              WMTS.<ows:Keywords><ows:Keyword>   ← Keywords
              WFS-2.<ows:Keywords>               ← Keywords
              WCS-2.<ows:Keywords>               ← Keywords
              Esri.documentInfo.Keywords         ← comma-joined Keywords
              OData.<Annotation Term="Org.OData.Core.V1.Tags"> ← Keywords array
Status:       ⚠️ slice 1 (needs landing)
```

### 2.3 Themes (DCAT-style categories)

```
Layer:        L1
V2 source:    Resource.Metadata.Themes[]
Fallback:     []
Standards:    OGC-API-Records.record.themes      ← Themes (DCAT theme URIs or labels)
              DCAT (via Records).dcat:theme      ← Themes
              STAC.collection.summaries["theme"] ← Themes (when present; non-standard convention)
Status:       ⚠️ slice 1
```

### 2.4 Language

```
Layer:        L1
V2 source:    Resource.Metadata.Language (BCP-47 tag)
Fallback:     Service.Metadata.Language → "en"
Standards:    OGC-API-Records.record.language    ← Language
              OGC-API-Common landingPage.language ← Service.Language
              STAC.collection (no field; via summaries["language"])
              link.hreflang                      ← Language
Status:       ⚠️ slice 1
```

### 2.5 Labels (selectable k/v) vs Annotations (opaque k/v)

```
Layer:        L1
V2 source:    Resource.Metadata.Labels / Resource.Metadata.Annotations
Standards:    Kubernetes-convention; not exposed externally.
              Internal admin/discovery filtering only.
              When projecting to STAC.collection.summaries: Labels may render as
              summary keys with single-element values (case-by-case).
Status:       ✅ stable
```

---

## 3. Provenance & versioning

### 3.1 Created / Updated timestamps

```
Layer:        L1
V2 source:    Resource.Metadata.CreatedAt / UpdatedAt (DateTimeOffset?)
Fallback:     null (omit from response when both null)
Standards:    OGC-API-Records.record.created     ← CreatedAt (ISO 8601)
              OGC-API-Records.record.updated    ← UpdatedAt
              STAC.properties.created           ← CreatedAt (item-level)
              STAC.properties.updated           ← UpdatedAt
              STAC.collection.extent.temporal   ← UpdatedAt? (collection-level)
              Esri.editFieldsInfo.creationDate  ← CreatedAt (Esri Date as ms-since-epoch)
              Esri.editFieldsInfo.editDate      ← UpdatedAt
              OData.@odata.etag                 ← derived (see 3.3)
              GeoPackage.gpkg_contents.last_change ← UpdatedAt ?? CreatedAt
              KML.<atom:updated>                ← UpdatedAt
Derivation:   Esri Date format = milliseconds since Unix epoch
              ISO 8601 strings always include offset (Z for UTC)
Status:       ✅ stable
```

### 3.2 Graph revision

```
Layer:        L1
V2 source:    Graph.Revision (long, monotonic)
Standards:    OGC-API-* response Last-Modified header / ETag derivation
              STAC root / catalog ETag
              Esri service.currentVersion        ← Graph.Revision (cast)
              OData @odata.etag base             ← Graph.Revision
Derivation:   Server-wide monotonic counter; never exposed directly except
              implicitly via ETag headers.
Status:       ✅ stable
```

### 3.3 Per-resource ETag

```
Layer:        L3 (derived at render time)
V2 source:    derive("etag-v1", Resource.Metadata.Id, Resource.Metadata.UpdatedAt
                                    ?? Resource.Metadata.CreatedAt, Graph.Revision)
Standards:    OData.@odata.etag                 ← ETag string
              OGC-API-Features.GetItem.ETag header
              HTTP response ETag header on collection / item / style endpoints
Derivation:   ETag = "W/\"" + FNV1a(canonicalForm) + "\""
              where canonicalForm includes (resource-id, updated-at-or-created-at,
              graph-revision). Weak ETag because we don't compute byte-for-byte
              content hash.
Status:       ✅ stable (helper in MetadataV2GraphSnapshotExtensions)
```

### 3.4 Generation (optimistic concurrency)

```
Layer:        ❌ GAP — re-add proposed (deleted in slice 65/N, needs return)
V2 source:    Resource.Metadata.Generation (long?) — currently absent
Fallback:     (gap)
Standards:    OData If-Match header conflict resolution
              Admin endpoints PUT/PATCH if-match guards
              OGC-API-Features Part 4 Editing weak/strong ETag negotiation
Status:       ❌ documented gap; proposed re-add in slice 1
```

---

## 4. Licensing & contact

### 4.1 License

```
Layer:        L1 (after slice 1 promotion from Extensions["stac"]["license"])
V2 source:    Resource.Metadata.License (SPDX identifier or "proprietary")
Fallback:     Service.Metadata.License → "proprietary"
Standards:    STAC.collection.license            ← License (REQUIRED — SPDX or "proprietary" or "various")
              OGC-API-Records.record.license     ← License
              OGC-API-* link rel=license         ← derive URL from License (SPDX → spdx.org/licenses/{id})
              WMS.<AccessConstraints>            ← human-readable phrase from License
              WMS.<Fees>                         ← "none" unless Extensions["wms-legal"]["fees"]
              WMTS.<ows:AccessConstraints>       ← same
              WCS-2.<ows:AccessConstraints>      ← same
              Esri.copyrightText                 ← Resource.Metadata.Attribution OR License (license used as fallback)
              Esri.documentInfo.Credits          ← Attribution
              GeoPackage.gpkg_contents.description (free-form; not standard slot)
Derivation:   - SPDX → URL: "https://spdx.org/licenses/" + License + ".html"
              - WMS access constraints text:
                  License=="proprietary"  → "Commercial use prohibited without license."
                  License starts with "CC-" → "Creative Commons license: " + License
                  License known SPDX      → "Distributed under " + License
                  License == "none"       → "No restrictions."
Status:       ⚠️ slice 1
```

### 4.2 Attribution

```
Layer:        L1
V2 source:    Resource.Metadata.Attribution
Fallback:     Service.Metadata.Attribution
Standards:    OGC-API-Features.collection.attribution ← Attribution
              OGC-API-Records.record.attribution     ← Attribution (when present)
              STAC.collection.providers[?].name      ← Attribution split into provider records
              WMS.<AttributionURL>                   ← Attribution string with URL detection
              Esri.copyrightText                     ← Attribution
              Esri.documentInfo.Credits              ← Attribution
Status:       ⚠️ slice 1
```

### 4.3 ContactPoint

```
Layer:        L1
V2 source:    Resource.Metadata.ContactPoint ({ Name, Email, Url })
Fallback:     Service.Metadata.ContactPoint
Standards:    OGC-API-Common landingPage.contact     ← ContactPoint
              OGC-API-Records.record.contacts[]      ← [ContactPoint] (single-element list)
              WMS.<ContactInformation>               ← multi-element block:
                <ContactPersonPrimary><ContactPerson>{Name}
                <ContactElectronicMailAddress>{Email}
                (no Url; OWS Service Contact has provider site URL elsewhere)
              WMTS.<ows:ServiceContact>              ← OWS Service Contact shape
              WFS-2.<ows:ServiceContact>             ← same
              WCS-2.<ows:ServiceContact>             ← same
              Esri.documentInfo.Author               ← ContactPoint.Name
              STAC.collection.providers[?]           ← split per role; ContactPoint.Url → providers[].url
Derivation:   STAC providers split: when ContactPoint set, emit one provider
              with roles=["host"] (server hosting). Add roles=["producer"] entry
              when Publisher is set (4.4) and differs from ContactPoint.
Status:       ⚠️ slice 1
```

### 4.4 Publisher (data producer/source)

```
Layer:        L1
V2 source:    Resource.Metadata.Publisher (string or { Name, Url })
Fallback:     null (omit)
Standards:    OGC-API-Records.record.publisher       ← Publisher
              DCAT.dcat:publisher                    ← Publisher (URI when Url set)
              STAC.collection.providers[role=producer] ← Publisher
              Esri.documentInfo.Subject              ← Publisher (loose mapping)
Status:       ⚠️ slice 1
```

---

## 5. Spatial reference & extent

### 5.1 Bounding box

```
Layer:        L1
V2 source:    Resource.Spatial.Bbox ({ West, South, East, North })
Fallback:     compute from data when null and asked (lazy)
Standards:    OGC-API-Features.extent.spatial.bbox    ← [[W, S, E, N]] (multi-bbox supported)
              STAC.collection.extent.spatial.bbox[]   ← [[W, S, E, N]] (multi-polygon ok)
              WMS.<EX_GeographicBoundingBox>          ← always WGS84 (transform if Spatial.SpatialReference != WGS84)
              WMS.<BoundingBox CRS="...">             ← per declared CRS
              WMTS.<ows:WGS84BoundingBox>             ← WGS84-transformed
              WFS-2.<ows:WGS84BoundingBox>            ← same
              WCS-2.<gml:boundedBy><gml:Envelope>     ← srsName=Resource.Spatial.SpatialReference
              Esri-FeatureServer.extent               ← { xmin, ymin, xmax, ymax, spatialReference: { wkid: srid, latestWkid: srid } }
              Esri-FeatureServer.fullExtent           ← same as extent
              Esri-MapServer.fullExtent / initialExtent ← same; initialExtent may be different (admin-settable)
              Esri-ImageServer.extent                 ← same
              GeoJSON.bbox                            ← [W, S, E, N] (CRS84)
              GeoPackage.gpkg_contents.min_x/min_y/max_x/max_y ← Bbox
              FlatGeobuf.header.envelope              ← Bbox
              GeoParquet.geo.columns[geom].bbox       ← Bbox
              KML.<Region><LatLonAltBox>              ← Bbox in CRS84
              Shapefile (.shp header)                 ← Bbox in native CRS
Derivation:   WGS84/CRS84 transform when Resource.Spatial.SpatialReference != WGS84:
                use CoordinateTransformer.TransformBbox(srid → 4326).
              Latest WKID: SridLookup.LatestWkid(srid) — Esri prefers latest equivalents.
Status:       ✅ stable
```

### 5.2 CRS / SpatialReference

```
Layer:        L1
V2 source:    Resource.Spatial.SpatialReference ({ Srid, Crs, IsGeographic })
Fallback:     null → derive default (WGS84) for response
Standards:    OGC-API-Features Part 1.collection.extent.spatial.crs ← Crs URI
              OGC-API-Features Part 2.collection.crs[] ← [Crs URI, ...] (from SupportedCrs after slice 4)
              WMS.<CRS>                                ← Crs URIs (one element each)
              WMTS.<TileMatrixSet>.SupportedCRS        ← Crs URI
              WFS-2.<DefaultCRS> / <OtherCRS>          ← primary + SupportedCrs[]
              WCS-2.<gml:Envelope srsName="...">       ← Crs URI on every envelope
              Esri-FeatureServer.spatialReference      ← { wkid: Srid, latestWkid: SridLookup.LatestWkid(Srid) }
              GeoParquet.geo.columns[].crs             ← PROJJSON of Crs
              FlatGeobuf.header.crs                    ← { org: "EPSG", code: Srid, name: ..., wkt: ... }
              GeoPackage.gpkg_spatial_ref_sys row      ← row with srs_id, organization, organization_coordsys_id, definition (WKT)
              KML                                      ← always CRS84 (transform if needed)
              GML.srsName                              ← Crs URI on every geometry
              Shapefile.prj                            ← WKT
              COG TIFF tags                            ← EPSG code in ProjectedCSTypeGeoKey / GeographicTypeGeoKey
Derivation:   Crs URI format: "http://www.opengis.net/def/crs/EPSG/0/{srid}"
              WKT: WktConverter.SridToWkt(srid)
              PROJJSON: ProjJsonConverter.SridToProjJson(srid)
              Latest WKID: SridLookup.LatestWkid(srid)
Status:       ✅ stable
```

### 5.3 Geometry type

```
Layer:        L1
V2 source:    Resource.Spatial.GeometryType (MetadataV2GeometryType enum)
Fallback:     None (tabular resource)
Standards:    OGC-API-Features.collection (informational; no required slot)
              Esri-FeatureServer.geometryType         ← "esriGeometryPoint" / "esriGeometryMultipoint" / "esriGeometryPolyline" / "esriGeometryPolygon" / "esriGeometryEnvelope" / "esriGeometryMultipatch"
              STAC (informational; not exposed at collection level)
              GeoParquet.geo.columns[].geometry_types ← ["Point"] etc. (SF type names)
              FlatGeobuf.header.geometry_type         ← FlatGeobuf enum
              GeoPackage.gpkg_geometry_columns.geometry_type_name ← "POINT" etc.
              WFS-2 DescribeFeatureType               ← gml:Point / gml:LineString / gml:Polygon / gml:Multi* / gml:GeometryCollection element types
              GML.gml:Point etc.                      ← determined per feature element
Derivation:   Esri mapping:
                Point → esriGeometryPoint
                MultiPoint → esriGeometryMultipoint
                LineString | MultiLineString → esriGeometryPolyline
                Polygon | MultiPolygon → esriGeometryPolygon
                GeometryCollection → esriGeometryMultipatch (best effort)
                Mixed → esriGeometryPoint with note
              SF / FlatGeobuf: passthrough enum name
Status:       ✅ stable
```

### 5.4 Multi-CRS support (Part 2)

```
Layer:        L1 (after slice 4)
V2 source:    Resource.Spatial.SupportedCrs[] (list of MetadataV2SpatialReference)
              Resource.Spatial.StorageCrs (the on-disk CRS, often != response CRS)
              Resource.Spatial.StorageCrsCoordinateEpoch (decimal year for time-varying CRS)
Fallback:     [Resource.Spatial.SpatialReference] when SupportedCrs empty
Standards:    OGC-API-Features Part 2 (CRS).collection.crs[]                  ← SupportedCrs
              OGC-API-Features Part 2.collection.storageCrs                  ← StorageCrs.Crs URI
              OGC-API-Features Part 2.collection.storageCrsCoordinateEpoch  ← StorageCrsCoordinateEpoch
              WFS-2.<DefaultCRS> / <OtherCRS>                                ← StorageCrs + SupportedCrs
              Esri-FeatureServer.advancedQueryCapabilities.supportsReturningQueryGeometry ← (irrelevant — Esri uses single CRS per service)
              GeoParquet.geo.columns[].epoch                                 ← StorageCrsCoordinateEpoch
              GeoParquet.geo.columns[].crs                                   ← StorageCrs
Status:       ❌ gap; slice 4
```

---

## 6. Temporal extent & time fields

### 6.1 Time field names

```
Layer:        L1
V2 source:    Resource.Temporal.{StartTimeField, EndTimeField, TrackIdField}
Fallback:     null (resource is non-temporal)
Standards:    Esri-FeatureServer.timeInfo.startTimeField  ← StartTimeField
              Esri-FeatureServer.timeInfo.endTimeField    ← EndTimeField
              Esri-FeatureServer.timeInfo.trackIdField    ← TrackIdField
              OGC-API-Features Part 1.collection.extent.temporal.interval ← derived from data
              WMS.<Dimension name="time"></Dimension>     ← derived extent (no field-name surface)
              STAC.collection.extent.temporal.interval[][] ← from Extent + computed
Status:       ✅ stable
```

### 6.2 Temporal extent

```
Layer:        L1
V2 source:    Resource.Temporal.Extent (MetadataV2TimeRange { Start, End })
Fallback:     compute lazily via TemporalExtentHelpers.TryResolveTemporalRangeV2Async
Standards:    OGC-API-Features.extent.temporal.interval[0][0] ← Extent.Start (ISO 8601 or null for open-start)
              OGC-API-Features.extent.temporal.interval[0][1] ← Extent.End
              OGC-API-Features.extent.temporal.trs            ← "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian"
              STAC.collection.extent.temporal.interval[0][0]  ← Extent.Start
              STAC.collection.extent.temporal.interval[0][1]  ← Extent.End
              WMS.<Dimension name="time">                     ← Extent.Start "/" Extent.End "/PT1S" (or computed period)
              Esri-FeatureServer.timeInfo.timeExtent           ← [Start.UnixMs, End.UnixMs]
Derivation:   Esri timeExtent uses ms-since-epoch
              ISO 8601: omit endpoint when null (open interval)
              Multi-interval extent: not currently supported (slice 6 to add)
Status:       ✅ stable
```

---

## 7. Schema fields (per-attribute)

### 7.1 Field name + type

```
Layer:        L1
V2 source:    Field.Name + Field.Type (MetadataV2FieldType enum)
Standards:    OGC-API-Features Part 5.schema (JSON Schema).properties[].type ← MetadataV2FieldType → JSON Schema type:
                String → "string"; Integer/BigInteger → "integer"; Double/Float → "number";
                Boolean → "boolean"; DateTime/Date/Time → "string" + format; Uuid → "string" + format=uuid;
                Json → "object"; Binary → "string" + contentEncoding=base64; Geometry/Geography → ref to geom schema
              Esri-FeatureServer.fields[].name                          ← Field.Name
              Esri-FeatureServer.fields[].type                          ← MetadataV2FieldType → esriField*:
                String → esriFieldTypeString; Integer → esriFieldTypeInteger; BigInteger → esriFieldTypeBigInteger;
                Double → esriFieldTypeDouble; Float → esriFieldTypeSingle; Boolean → esriFieldTypeSmallInteger (0/1);
                DateTime → esriFieldTypeDate; Date → esriFieldTypeDateOnly; Time → esriFieldTypeTimeOnly;
                Uuid → esriFieldTypeGUID; Json → esriFieldTypeString (serialized); Binary → esriFieldTypeBlob;
                Geometry → esriFieldTypeGeometry
              OData.<Property Name=... Type=...>                         ← MetadataV2FieldType → Edm.*:
                String → Edm.String; Integer → Edm.Int32; BigInteger → Edm.Int64;
                Double → Edm.Double; Float → Edm.Single; Boolean → Edm.Boolean;
                DateTime → Edm.DateTimeOffset; Date → Edm.Date; Time → Edm.TimeOfDay;
                Uuid → Edm.Guid; Json → Edm.String (or Edm.Stream); Binary → Edm.Binary;
                Geometry → Edm.Geometry (Point/LineString/Polygon variants)
              WFS-2 DescribeFeatureType.xsd:element type                 ← MetadataV2FieldType → xsd:* per OGC mapping
              GeoParquet.<column logical-type>                           ← Parquet primitive + logical type
              FlatGeobuf.columns[].type                                  ← FlatGeobuf enum
              GeoPackage.gpkg_data_columns (informational rows)          ← Field.Name + Field.Title
              Shapefile.dbf field type                                   ← C/N/F/D/L (lossy reduction)
Status:       ✅ stable
```

### 7.2 Field alias

```
Layer:        L1 (slice 2)
V2 source:    Field.Alias
Fallback:     Field.Title → Field.Name
Standards:    Esri-FeatureServer.fields[].alias                          ← Alias ?? Title ?? Name
              OData.<Annotation Term="Org.OData.Core.V1.Label">          ← Alias
              OGC-API-Features Part 5 schema.properties[].title          ← Alias ?? Title
              GeoPackage.gpkg_data_columns.title                         ← Alias ?? Title
Status:       ❌ slice 2
```

### 7.3 Field editable

```
Layer:        L1 (slice 2)
V2 source:    Field.Editable (bool, default true)
Standards:    Esri-FeatureServer.fields[].editable                       ← Editable
              OData.<Annotation Term="Org.OData.Core.V1.Immutable">      ← !Editable
              OGC-API-Features Part 4 (Editing).schema.properties[].readOnly ← !Editable
Status:       ❌ slice 2
```

### 7.4 Field length / maxLength

```
Layer:        L1 (slice 2)
V2 source:    Field.Length (int?) — for VARCHAR / character-typed fields
Standards:    Esri-FeatureServer.fields[].length                         ← Length
              OData.<Property MaxLength="...">                            ← Length
              OGC-API-Features Part 5 schema.properties[].maxLength      ← Length
              GeoPackage.gpkg_data_column_constraints                    ← maxLength constraint row
              Shapefile.dbf field length                                  ← Length
Status:       ❌ slice 2
```

### 7.5 Field default value

```
Layer:        L1 (slice 2)
V2 source:    Field.DefaultValue (JsonElement?)
Standards:    Esri-FeatureServer.fields[].defaultValue                   ← DefaultValue (typed by Field.Type)
              OData.<Property DefaultValue="...">                         ← DefaultValue serialized
              OGC-API-Features Part 5 schema.properties[].default        ← DefaultValue
              OGC-API-Features Part 4 (Editing) — used on POST when client omits
Status:       ❌ slice 2
```

### 7.6 Field domain (coded values / range)

```
Layer:        L1 (slice 2)
V2 source:    Field.Domain ({ Type, CodedValues[] | Range[] })
Standards:    Esri-FeatureServer.fields[].domain
                For coded-value: { type: "codedValue", name, codedValues: [{ code, name }] }
                For range:       { type: "range", name, range: [min, max] }
              OData.<Annotation Term="Org.OData.Core.V1.OptionalProperties.Values">
                                                                          ← CodedValues
              OGC-API-Features Part 5 schema.properties[].enum            ← CodedValues.code[]
              OGC-API-Features Part 5 schema.properties[].minimum/maximum ← Range[0]/Range[1]
              GeoPackage.gpkg_data_column_constraints                     ← range/enum rows
              JSON Schema.properties[].enum / .minimum / .maximum         ← Domain
Status:       ❌ slice 2
```

### 7.7 Field semantic roles

```
Layer:        L1
V2 source:    Field.SemanticRoles[] (string[]; canonical roles below)
Canonical roles:
  - "id.primary"        — primary identifier (resolves to Esri OBJECTID)
  - "id.global"         — global stable id (Esri globalIdField; OData @odata.id)
  - "geometry.primary"  — primary geometry column
  - "geometry.envelope" — derived envelope column
  - "temporal.start"    — start-time field (matches Resource.Temporal.StartTimeField)
  - "temporal.end"      — end-time field
  - "temporal.track"    — track id field
  - "editor.creator"    — created-by user (matches Editing.CreatorField)
  - "editor.editor"     — modified-by user
  - "editor.created-at" — creation timestamp
  - "editor.updated-at" — modification timestamp
  - "display.label"     — popup label field (matches Display.DisplayField)
  - "display.thumbnail" — preview image URL field
Standards:    Resolves all the per-protocol "this field plays role X" lookups
              from one declarative source.
Status:       ✅ stable
```

---

## 8. Display / render hints

### 8.1 Scale range

```
Layer:        L1 (slice 3)
V2 source:    Resource.Display.MinScale / MaxScale (double? denominators)
Standards:    WMS.<MinScaleDenominator>                                ← MinScale
              WMS.<MaxScaleDenominator>                                ← MaxScale
              WMTS — derived to TileMatrix range
              Esri-FeatureServer.minScale / maxScale                   ← MinScale / MaxScale
              Esri-MapServer.minScale / maxScale                       ← same
              OGC-API-Maps.collection.minScaleDenominator / max...     ← MinScale / MaxScale
              MapLibre style layers.minzoom / maxzoom                  ← scale-to-zoom conversion
Derivation:   Web Mercator scale→zoom: zoom ≈ log2(559082264.0287 / scale)
                rounded to nearest integer.
              When MinScale set: emit zoom = floor(log2(559082264.0287 / MinScale))
              When MaxScale set: emit zoom = ceil(log2(559082264.0287 / MaxScale))
Status:       ❌ slice 3
```

### 8.2 Default visibility

```
Layer:        L1 (slice 3)
V2 source:    Resource.Display.DefaultVisibility (bool, default true)
Standards:    Esri-FeatureServer.layer.defaultVisibility               ← DefaultVisibility
              Esri-MapServer.layers[].defaultVisibility                ← same
              MapLibre style.layers[].layout.visibility                ← "visible" / "none"
              WMS — implicit (no flag; default to all layers shown when no LAYERS=)
              OGC-API-Tiles — implicit
Status:       ❌ slice 3
```

### 8.3 Display field (popup label)

```
Layer:        L1 (slice 3)
V2 source:    Resource.Display.DisplayField
Fallback:     first field with SemanticRoles containing "display.label"
              → first field of type String with Name not equal to objectid
Standards:    Esri-FeatureServer.layer.displayField                    ← DisplayField
              Esri-MapServer.layers[].displayField                     ← same
              MapLibre style.symbol layers.text-field                  ← "{DisplayField}" expression
              OGC-API-Features (informational) — link rel=describedby
Status:       ❌ slice 3
```

### 8.4 Queryable

```
Layer:        L1 (slice 3)
V2 source:    Resource.Display.Queryable (bool, default true)
Standards:    WMS.<Layer queryable="1" / queryable="0">                ← Queryable
              OGC-API-Features.conformance includes /conf/queryables    ← when Queryable=true (per-collection allowance still implicit)
              OGC-API-Features Part 3 (Filter) declared per collection ← derived from Queryable + Service.Protocols
              Esri-FeatureServer.capabilities CSV includes "Query"     ← derived
Status:       ❌ slice 3
```

### 8.5 HasZ / HasM

```
Layer:        L1 (slice 3)
V2 source:    Resource.Display.HasZ / HasM (bool, default false)
Standards:    Esri-FeatureServer.hasZ / hasM                            ← HasZ / HasM
              FlatGeobuf.header.has_z / has_m                          ← HasZ / HasM
              GeoParquet.geo.columns[].geometry_types includes "Z" /"M" suffix variants
              GeoPackage.gpkg_geometry_columns.z / .m                  ← HasZ ? 1 : 0
              WKB type code (1000-series for Z, 2000 for M, 3000 for ZM) ← derived per geometry
Status:       ❌ slice 3
```

---

## 9. Filtering

### 9.1 Permanent filter

```
Layer:        L1
V2 source:    Resource.PermanentFilter ({ Expression, Language })
Fallback:     null (no permanent filter)
Standards:    Esri-FeatureServer.layer.definitionExpression             ← Expression (when Language="arcgis-sql")
              Esri-MapServer.layers[].definitionExpression              ← same
              OGC-API-Features Part 3 (Filter) — server-side; never exposed in response
              WFS-2 — server-side; never exposed
              OData — server-side; never exposed
              STAC — server-side
Derivation:   Expression is parsed by IFilterExpressionService and ANDed with
              per-request filter at query time.
              For Esri: emit raw arcgis-sql expression when Language matches;
              when Language="cql2-text" or "cql2-json", translate to arcgis-sql
              (lossy at edges).
Status:       ✅ stable
```

---

## 10. Relationships

### 10.1 Resource-to-resource relationship

```
Layer:        L1
V2 source:    Resource.Relationships[] (MetadataV2Relationship)
              Each carries: Id, Name, RelatedResourceId, Role ("origin" | "destination"),
              Cardinality ("one-to-one" | "one-to-many" | "many-to-many"),
              OriginField, DestinationField, EsriRelationshipId? (int)
Standards:    Esri-FeatureServer.layer.relationships[]                  ← Relationships with mappings:
                id          ← EsriRelationshipId ?? FNV1a(Id) mod 2^31
                name        ← Name
                relatedTableId ← snapshot.ResolveStorageLayerId(RelatedResourceId)
                role        ← Role
                keyField    ← OriginField (when Role="origin") / DestinationField (when "destination")
                cardinality ← Cardinality → "esriRelCardinalityOneToOne" etc.
              Esri-MapServer.layers[].relationships[]                   ← same
              OData.<NavigationProperty Name=Name Type=Collection(RelatedEntity)> ← Relationships
              OGC-API-Features (informational) — link rel=related        ← Relationships
              STAC.links[rel=related]                                    ← Relationships
              GeoPackage gpkg_extensions row + related_tables extension  ← Relationships
Status:       ✅ stable
```

---

## 11. Storage binding

Storage binding fields are internal-only and not exposed in any external API.
See §12 of the [crosswalk](metadata-v2-crosswalk.md#12-storage-binding--access)
for the storage-binding model. The only external rendering is via STAC
`assets` for STAC publications — covered in §17.5 below.

---

## 12. Styling

### 12.1 Style resource reference

```
Layer:        L1 (slice 5)
V2 source:    Resource.StyleResourceIds[] (re-add; [0] = primary)
Standards:    WMS.<Style>                                              ← one per StyleResourceId
              WMS.<Style is-default>                                   ← true on first
              WMTS.<Style isDefault="true">                            ← true on first
              OGC-API-Styles.styles[].id                               ← Resource.Metadata.Id (style-typed)
              OGC-API-Maps.styleId query parameter                     ← StyleResourceIds membership check
              Esri-FeatureServer.drawingInfo                           ← StyleResourceIds[0] body
              Esri-MapServer.layers[].drawingInfo                      ← same
              MapLibre style document                                  ← StyleResourceIds[0] body
Status:       ❌ slice 5
```

### 12.2 Style body (multi-encoding)

```
Layer:        L1 (slice 5)
V2 source:    Resource.Style.Encodings[] on Type=Style resources
              Encoding values: "mapbox-style", "sld-1.0.0", "sld-1.1.0",
                "esri-drawing-info", "esri-image-renderer", "3d-tiles-styling"
Standards:    OGC-API-Styles.<style>.<encoding-link href>              ← Encodings[*]
              WMS GetStyles                                            ← prefer SLD encoding when present;
                                                                          generate from mapbox-style otherwise
              WMS SLD_BODY parameter                                   ← convert mapbox-style → sld at render time
              Esri-FeatureServer.drawingInfo.renderer                  ← prefer esri-drawing-info encoding;
                                                                          generate from mapbox-style otherwise
              Esri-ImageServer renderingRule                           ← esri-image-renderer encoding
              MapLibre tile style URL                                  ← mapbox-style encoding (raw passthrough)
              3D Tiles styling                                         ← 3d-tiles-styling encoding
Derivation:   See ADR-0002 (MapLibre canonical); converters live in render layer.
Status:       ❌ slice 5
```

---

## 13. Capabilities / supported operations

### 13.1 Service-level protocols

```
Layer:        L1
V2 source:    Service.Protocols[] (list of ServiceProtocols.* string constants)
              + Service.PrimaryProtocol (derived; = Protocols[0])
Standards:    OGC-API-* /conformance                                    ← derived list of conformance class URIs:
                from each Service.Protocols entry, map to declared conformance classes
                (e.g. ServiceProtocols.OgcFeatures → /conf/core, /conf/oas30, /conf/geojson,
                + Part 2/3/4/5 when admin opts in)
              Esri-FeatureServer.capabilities CSV                       ← derived from Resource.Editing.*:
                "Query" — always (when any Resource.Display.Queryable)
                "Create" — when any Resource.Editing.CanModify
                "Update" — same
                "Delete" — same
                "Editing" — convenience prefix when any modify capability present
              Esri-MapServer.capabilities                               ← same
              Esri-ImageServer.capabilities                             ← "Image,Catalog,Metadata"
              WMS.<Request><Operation>                                  ← derived from Service.Protocols
              WFS-2.<ows:OperationsMetadata>                            ← derived
              WCS-2.<ows:OperationsMetadata>                            ← derived
              OData.$metadata.<Annotation Term="...">                   ← derived
Status:       ✅ stable
```

### 13.2 Editing capabilities (slice 3)

```
Layer:        L1 (slice 3)
V2 source:    Resource.Editing ({ GlobalIdField, CreatorField, CreatedAtField,
                EditorField, UpdatedAtField, CanModify, SupportsAttachments,
                SupportsRelatedRecords, HasLabels })
Standards:    Esri-FeatureServer.layer.globalIdField                    ← GlobalIdField
              Esri-FeatureServer.layer.editorTrackingInfo               ← from Creator/Created/Editor/Updated fields
              Esri-FeatureServer.layer.editFieldsInfo                   ← same (compatibility alias)
              Esri-FeatureServer.layer.canModifyLayer                   ← CanModify
              Esri-FeatureServer.layer.hasLabels                        ← HasLabels (slice 3)
              Esri-FeatureServer.layer.supportsAttachments              ← SupportsAttachments
              Esri-FeatureServer.layer.supportsRelatedRecords           ← SupportsRelatedRecords
              OGC-API-Features Part 4 (Editing).conformance class       ← derive from CanModify across collections
              OData.@odata.id field                                     ← derived from GlobalIdField when set
              OData.<Annotation Term="Org.OData.Capabilities.V1.ChangeTracking"> ← derive from editor tracking fields presence
Status:       ❌ slice 3
```

---

## 14. Service settings (operational limits)

```
Layer:        L1 (slice 4)
V2 source:    Service.Settings ({ MaxRecordCount, DefaultRecordCount,
                MaxImageWidth, MaxImageHeight, DefaultDpi, MaxFeaturesPerLayer,
                DefaultFormat, DefaultTileMatrixSet, SupportedFormats[] })
Standards:    Esri-FeatureServer.maxRecordCount                          ← MaxRecordCount
              Esri-FeatureServer.standardMaxRecordCount                  ← MaxRecordCount
              Esri-FeatureServer.supportedQueryFormats                   ← join SupportedFormats with ","
              Esri-MapServer.maxImageWidth / maxImageHeight              ← MaxImageWidth / MaxImageHeight
              Esri-MapServer.maxRecordCount                              ← MaxRecordCount
              Esri-ImageServer.maxImageWidth / maxImageHeight            ← same
              WMS.<MaxWidth> / <MaxHeight>                              ← MaxImageWidth / MaxImageHeight
              WMS.<Format>                                              ← SupportedFormats
              WFS-2.<wfs:Constraint name="CountDefault">                ← DefaultRecordCount
              WFS-2.<wfs:Constraint name="ImplementsResultPaging">      ← always TRUE
              OGC-API-Features.collection.maxItems                       ← MaxRecordCount
              OGC-API-Features.default limit                             ← DefaultRecordCount
              OGC-API-Tiles tile matrix set                             ← DefaultTileMatrixSet
              OData.<Annotation Term="Org.OData.Capabilities.V1.TopSupported"> ← MaxRecordCount when set
Status:       ❌ slice 4
```

---

## 15. Editing tracking

Covered under 13.2 (Resource.Editing slot — slice 3).

---

## 16. Links

```
Layer:        L1 (slice 1)
V2 source:    Resource.Metadata.Links[] / Service.Metadata.Links[]
              Each link: { Href, Rel, Type?, Title?, Hreflang? }
Standards:    OGC-API-Common.links[]                                    ← Links[] + computed self/data/items/queryables/schema/etc.
              OGC-API-Features.collection.links[]                       ← same
              OGC-API-Records.record.links[]                            ← same + via/child/parent
              OGC-API-Styles.style.links[]                              ← Links[] + stylesheet links per encoding
              STAC.collection.links[] / item.links[]                    ← same + STAC-required rels (self, parent, root, items, search)
              OData.@odata.context / @odata.nextLink                    ← computed pagination + Links[] for self
              WMS.<DataURL> / <FeatureListURL> / <MetadataURL>          ← Links[?Rel="data"] / [Rel="enclosure"] / [Rel="describedby"]
              KML.<atom:link href=...>                                  ← Links[]
Derivation:   Computed links per request (self, alternate format, next page,
              prev page) are added by Layer 3 builders, not stored in V2.
              Stored Links[] is for static external references (documentation
              URL, source dataset URL, contact URL, terms-of-service URL).
Status:       ❌ slice 1
```

---

## 17. Tiling-specific

### 17.1 Tile matrix set

```
Layer:        L1 (slice 4)
V2 source:    Service.Settings.DefaultTileMatrixSet (string identifier)
Fallback:     "WebMercatorQuad" (de-facto default)
Standards:    OGC-API-Tiles.tileMatrixSetURI                            ← URI lookup from identifier
              OGC-API-Tiles.tileMatrixSets[]                            ← list of supported (from server registry)
              WMTS.<TileMatrixSet>                                      ← full geometry of every level (computed from identifier)
              Esri tileInfo.lods[]                                       ← computed from identifier + min/max
              MBTiles.metadata.format = "pbf"|"png"|"jpg"               ← Service.Settings.DefaultFormat
              PMTiles.header.tile_type                                  ← same
              TileJSON tilejson="3.0.0"                                 ← computed
Status:       ❌ slice 4
```

### 17.2 Min/max zoom

```
Layer:        L3 (derived from Display.MinScale/MaxScale + tile matrix set)
V2 source:    Resource.Display.MinScale / MaxScale (slice 3)
Standards:    MBTiles.metadata.minzoom / maxzoom                        ← derived
              PMTiles.header.min_zoom / max_zoom                        ← derived
              TileJSON.minzoom / maxzoom                                ← derived
              OGC-API-Tiles.tileMatrixSetLimits                         ← derived
Derivation:   See 8.1 — scale→zoom for Web Mercator.
Status:       ✅ stable (after slice 3 lands Display)
```

### 17.3 Tile format

```
Layer:        L1 (slice 4)
V2 source:    Service.Settings.DefaultFormat
Standards:    MBTiles.metadata.format                                   ← DefaultFormat ("pbf" | "png" | "jpg" | "webp")
              PMTiles.header.tile_type                                  ← DefaultFormat mapped to enum
              TileJSON.format                                           ← DefaultFormat
              OGC-API-Tiles supportedTileFormats                        ← Service.Settings.SupportedFormats
Status:       ❌ slice 4
```

### 17.4 Vector layers metadata (MBTiles vector_layers, PMTiles)

```
Layer:        L3 (derived from Resource.SchemaFields)
V2 source:    Resource.SchemaFields where Type != Geometry/Geography
Standards:    MBTiles.metadata.json.vector_layers                       ← derived from SchemaFields:
                id ← Resource.Metadata.Name
                fields ← { Field.Name: Field.Title ?? "" }
                minzoom/maxzoom ← derived (17.2)
              PMTiles.tilejson.vector_layers                            ← same
              TileJSON.vector_layers                                    ← same
Status:       ✅ stable
```

### 17.5 STAC assets (per-item storage references)

```
Layer:        L3 (per-item rendering)
V2 source:    StorageBinding.Locator (Uri) + StorageBinding.Type + StorageBinding.Capabilities
              + Item-level metadata (file path, size, role)
Standards:    STAC.item.assets[]                                        ← per asset:
                href ← per-asset URL
                title ← Resource.Metadata.Title + suffix
                type ← MIME from StorageBinding.Type
                roles ← derived: ["data" | "overview" | "thumbnail" | "metadata"]
                file:size ← computed
                eo:bands ← Extensions["stac"]["eo"]["bands"] (when present)
                proj:epsg ← Resource.Spatial.SpatialReference.Srid
Status:       ✅ stable
```

---

## 18. Raster / coverage-specific

### 18.1 Pixel type / band count

```
Layer:        ❌ GAP (deferred — slice 6 when raster handlers consume)
V2 source:    Resource.Raster.PixelType / BandCount / NoData[] (not yet defined)
Standards:    Esri-ImageServer.pixelType                                ← PixelType → esriPixelType*
              Esri-ImageServer.bandCount                                ← BandCount
              Esri-ImageServer.noDataValue                              ← NoData
              WCS-2.<gmlcov:rangeType>                                  ← BandCount + per-band type
              OGC-API-Coverages.coverage.coverageRangeType              ← derived per-band
              COG TIFF tags BitsPerSample, SampleFormat                  ← PixelType
              GeoTIFF GDAL_NODATA tag                                   ← NoData
              Zarr.fill_value                                            ← NoData
              NetCDF._FillValue                                          ← NoData per variable
Status:       ❌ gap (slice 6)
```

---

## Open issues

Every row marked ❌ corresponds to a typed slot proposal in the
[crosswalk](metadata-v2-crosswalk.md). Landing the 5 numbered slices
fills every ❌ except 18.1 (raster slot deferred). Slice numbering:

| Slice | Adds |
|---|---|
| 1 | `Metadata.Keywords/Themes/Language/ContactPoint/Publisher/License/Attribution/Links`; promote license/attribution out of `Extensions["stac"]` |
| 2 | `Field.{Alias, Editable, Length, DefaultValue, Domain, SqlType}` |
| 3 | `Resource.Display.*`; `Resource.Editing.*` |
| 4 | `Service.Settings.*`; `Resource.Spatial.{SupportedCrs, StorageCrs, StorageCrsCoordinateEpoch}` |
| 5 | `Resource.StyleResourceIds[]` (re-add); `Resource.Style` + `MetadataV2StyleEncoding[]` |
| 6 (deferred) | `Resource.Raster` slot |

## Conformance

When a `Map*ResponseV2` builder emits a field listed above, its implementation
**MUST** read from the V2 source per this document. Deviations require:
1. An updated row here, OR
2. A justified `// MAPPING-V2-DEVIATION: ...` code comment explaining why the
   builder differs (e.g. backward-compat with a specific test fixture).

CI architecture tests (`tests/dotnet/Honua.Architecture.Tests/`) should grow
to enforce that builders touching wire-format fields named in this document
trace back to the listed V2 source.
