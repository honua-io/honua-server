# Metadata v2 ↔ API & package format crosswalk

Concept-by-concept semantic bridge between the V2 canonical graph and every
API/package format honua-server projects metadata into. Used to validate that
the V2 model can losslessly round-trip every concept these standards expose.

## Coverage legend
- ✅ — V2 has a typed slot, lossless mapping
- ⚠️ — V2 covers it via the open `Extensions` / `Options` bag (works, untyped)
- ❌ — V2 has no slot today; gap requires a new V2 field
- ⊘ — concept doesn't apply to the standard

## Scope
- **APIs**: OGC API - Features / Maps / Tiles / Coverages / Records / Styles / Common; WMS 1.3.0; WMTS 1.0.0; WFS 2.0; WCS 2.0; Esri FeatureServer / MapServer / ImageServer (GeoServices REST); STAC API 1.0; OData v4
- **Package formats**: GeoJSON (RFC 7946); GeoParquet; FlatGeobuf; GeoPackage; KML; GML 3.2.1; Shapefile; MBTiles; PMTiles; Cloud-Optimized GeoTIFF; Zarr; NetCDF; STAC items/collections; Esri JSON

---

## 1. Identity & naming

| V2 canonical | Status |
|---|---|
| `Resource.Metadata.Id` (stable, unique within graph) | ✅ |
| `Resource.Metadata.Name` (slug, machine-friendly) | ✅ |
| `Resource.Metadata.Title` (human-readable) | ✅ |
| `Publication.Identifier.Value` (protocol-facing route key) | ✅ |
| `Publication.Identifier.IsNumeric` (Esri-style numeric vs name) | ✅ |
| `Publication.Identifier.PathOverride` (full URL path override) | ✅ |

| Standard | Maps to |
|---|---|
| OGC API - Features | `collection.id` ← `Identifier.Value` (when not numeric) or `Metadata.Name`; `collection.title` ← `Metadata.Title` |
| OGC API - Maps/Tiles/Coverages | `collection.id` same as Features |
| OGC API - Records | `record.id` ← `Metadata.Id`; `record.title` ← `Metadata.Title` |
| OGC API - Styles | `style.id` ← `Metadata.Name`; `style.title` ← `Metadata.Title` (style-typed resources) |
| WMS | `<Name>` ← `OgcClassicRequestHelpers.GetWmsLayerName(resource, publication)`; `<Title>` ← `Metadata.Title` |
| WMTS | `<ows:Identifier>` ← `Identifier.Value`; `<ows:Title>` ← `Metadata.Title` |
| WFS 2.0 | `<wfs:Name>` ← namespaced (`prefix:Metadata.Name`); `<wfs:Title>` ← `Metadata.Title` |
| WCS 2.0 | `<wcs:CoverageId>` ← `Identifier.Value`; `<ows:Title>` ← `Metadata.Title` |
| Esri FeatureServer | layer `id` ← `Identifier.LayerIndex`; `name` ← `Metadata.Name` |
| Esri MapServer | layer `id` ← `Identifier.LayerIndex`; `name` ← `Metadata.Name` |
| Esri ImageServer | service-level; `name` ← `Metadata.Name` |
| STAC | `collection.id` ← `Metadata.Id`; `collection.title` ← `Metadata.Title` |
| OData | entity set name ← `Metadata.Name`; entity type ← derived |
| GeoJSON | n/a at feature level; collection-level via `properties.id` |
| GeoParquet | `geo.name` (file metadata) |
| FlatGeobuf | header `name` |
| GeoPackage | `gpkg_contents.identifier` / `gpkg_contents.table_name` |
| KML | `<Document><name>` |
| GML 3.2.1 | `gml:name` |
| Shapefile | filename ← `Metadata.Name`; metadata in sidecar `.xml` |

**Verdict**: ✅ Identity is fully typed and covered everywhere.

---

## 2. Description & discovery

| V2 canonical | Status |
|---|---|
| `Resource.Metadata.Description` | ✅ |
| `Resource.Metadata.Labels` (k/v selectors) | ✅ |
| `Resource.Metadata.Annotations` (k/v opaque) | ✅ |
| `Resource.Metadata.Keywords` (discovery tags) | ❌ — currently overloaded as `Labels.Keys` |
| `Resource.Metadata.Themes` (DCAT theme categories) | ❌ |
| `Resource.Metadata.Language` (BCP-47) | ❌ |
| `Service.Metadata.Description` / `Keywords` / `Themes` | ❌ (same gaps as Resource) |

| Standard | Maps to |
|---|---|
| OGC API - Features | `collection.description` ← `Description`; `collection.keywords` ← `Keywords` (Part 1 §7.13) |
| OGC API - Records | `record.description`, `record.keywords`, `record.themes`, `record.language` — **all required for cataloguing** |
| OGC API - Common | `landingPage.description` ← `Service.Description` |
| WMS | `<Abstract>` ← `Description`; `<KeywordList>` ← `Keywords` |
| WMTS | `<ows:Abstract>` ← `Description`; `<ows:Keywords>` ← `Keywords` |
| WFS 2.0 | `<wfs:Abstract>` ← `Description`; `<ows:Keywords>` |
| WCS 2.0 | `<ows:Abstract>` ← `Description`; `<ows:Keywords>` |
| Esri FeatureServer | `serviceDescription`, `description`, `documentInfo.Keywords`, `documentInfo.Subject`, `documentInfo.Category` |
| Esri MapServer | same plus `mapName`, `documentInfo.AntialiasingMode` |
| Esri ImageServer | `description`, `serviceDataType`, `documentInfo.*` |
| STAC | `collection.description` ← required; `collection.keywords` ← optional; `collection.themes` ← optional |
| OData | `<Summary>`, `<LongDescription>` annotations on EntityType/EntitySet |
| GeoJSON | n/a |
| GeoParquet | `geo.description` (file metadata extension) |
| FlatGeobuf | header `description` |
| GeoPackage | `gpkg_contents.description` |
| KML | `<description>` |
| GML | `gml:description`; ISO 19139 metadata sidecar |

**Verdict**: ⚠️ Description covered. Keywords/Themes/Language are gaps — currently leaking through `Labels` (wrong selector semantics) or `Extensions["stac"]`. **Promote to `Metadata` block.**

---

## 3. Provenance & versioning

| V2 canonical | Status |
|---|---|
| `Resource.Metadata.CreatedAt` (DateTimeOffset?) | ✅ |
| `Resource.Metadata.UpdatedAt` (DateTimeOffset?) | ✅ |
| `Graph.Revision` (monotonic) | ✅ |
| `Graph.GeneratedAt` (snapshot timestamp) | ✅ |
| `Style.StyleVersion` (proposed) | (see styles slice) |
| `Resource.Metadata.Generation` (optimistic concurrency) | ❌ — deleted in slice 65/N |
| Per-resource ETag | ❌ — derived from `(Resource.Metadata.Id, Graph.Revision)` ad-hoc |

| Standard | Maps to |
|---|---|
| OGC API - Features | `collection.itemType`, links `rel=alternate` for versioning; ETag headers ← Graph.Revision-derived |
| OGC API - Records | `record.created`, `record.updated`, `record.published` |
| Esri FeatureServer | `currentVersion`, `serviceItemId`, layer-level `cimVersion` |
| STAC | `properties.datetime`, `properties.created`, `properties.updated` (Item); `extent.temporal.interval[0]` (Collection) |
| OData | `@odata.etag` ← per-entity hash |
| GeoPackage | `gpkg_contents.last_change` |
| KML | `<atom:updated>` |
| GML | `gml:identifier` plus app-schema-specific provenance |

**Verdict**: ⚠️ Created/Updated covered. **Generation** (optimistic concurrency token) is a gap I introduced by deleting it; OData and admin APIs need it for if-match. Re-add as opt-in field. **Per-resource ETag** should be a typed extension method on Resource that derives from `(Id, UpdatedAt ?? CreatedAt ?? GraphRevision)`.

---

## 4. Licensing & attribution

| V2 canonical | Status |
|---|---|
| `Resource.Metadata.License` (SPDX or "proprietary") | ❌ — currently in `Extensions["stac"]["license"]` |
| `Resource.Metadata.Attribution` (display string) | ❌ |
| `Service.Metadata.License` | ❌ |
| `Service.Metadata.Attribution` | ❌ |

| Standard | Maps to |
|---|---|
| OGC API - Features | `collection.attribution` (informative); `licence` link rel |
| OGC API - Records | `record.license` (SPDX) — **required for many catalogs** |
| WMS | `<AttributionURL>`, `<AccessConstraints>`, `<Fees>` |
| WMTS | `<ows:AccessConstraints>`, `<ows:Fees>` |
| Esri FeatureServer | `copyrightText`, `documentInfo.Credits` |
| Esri MapServer | `copyrightText`, `mapName`, `documentInfo.Credits` |
| STAC | `collection.license` (**required**), `collection.providers[]` with `roles`, `attribution` |
| OData | `<Annotation Term="org.example.License">` |
| GeoPackage | `gpkg_contents.description` (free-form) |
| GeoJSON RFC 7946 | non-standard, by convention `"license"` at `FeatureCollection` level |
| KML | `<Document><atom:author>` |

**Verdict**: ❌ Real gap. STAC and OGC API Records both treat license as first-class. Promote `License` + `Attribution` to `Metadata` block on Resource and Service. Currently we have it leaking through `Extensions["stac"]` — wrong place.

---

## 5. Contact & publisher

| V2 canonical | Status |
|---|---|
| `Metadata.ContactPoint` ({Name, Email, Url}) | ❌ |
| `Resource.Metadata.Publisher` / `Provider` | ❌ |

| Standard | Maps to |
|---|---|
| OGC API - Records | `record.contacts[]` (name, organization, position, role, email, phone, address) |
| OGC API - Common | landing page `contact` |
| WMS | `<ContactInformation>` with `<ContactPersonPrimary>`, `<ContactAddress>`, `<ContactVoiceTelephone>`, `<ContactElectronicMailAddress>` |
| WMTS | `<ows:ServiceContact>` |
| WFS / WCS | same `<ows:ServiceContact>` |
| Esri | `documentInfo.Author`, `documentInfo.Credits` |
| STAC | `providers[].name`, `providers[].url`, `providers[].roles[]` (producer/processor/licensor/host) |
| DCAT (via Records) | `dcat:publisher`, `dcat:contactPoint` |
| OData | annotation `org.example.Contact` |

**Verdict**: ❌ Gap. Catalog endpoints (OGC Records, STAC, DCAT) want this. Add `Metadata.ContactPoint` + `Metadata.Publisher` (or a list, since STAC has multiple providers with roles).

---

## 6. Spatial reference & extent

| V2 canonical | Status |
|---|---|
| `Resource.Spatial.SpatialReference` ({Srid, Crs, IsGeographic}) | ✅ |
| `Resource.Spatial.GeometryType` (enum) | ✅ |
| `Resource.Spatial.Bbox` ({West, South, East, North}) | ✅ |
| `Resource.Spatial.PrimaryGeometryField` | ✅ |
| `Resource.Spatial.SupportedCrs[]` (multi-CRS) | ❌ — gap |
| `Resource.Spatial.StorageCrs` (native storage CRS, ≠ response CRS) | ❌ — gap |
| `Resource.Spatial.StorageCrsCoordinateEpoch` (time-varying CRS) | ❌ — gap |
| `Service.SpatialReference` (service-level output CRS) | ✅ |
| `Service.Settings.DefaultTileMatrixSet` (proposed) | (see settings slice) |

| Standard | Maps to |
|---|---|
| OGC API - Features Part 1 | `extent.spatial.bbox[]`, `extent.spatial.crs` |
| OGC API - Features Part 2 (CRS) | `crs[]` (supported), `storageCrs`, `storageCrsCoordinateEpoch` — **gaps above directly map** |
| WMS 1.3 | `<EX_GeographicBoundingBox>` (WGS84), per-layer `<CRS>` list, per-layer `<BoundingBox CRS="…">` |
| WMTS | `<ows:WGS84BoundingBox>`, `<TileMatrixSet>` references |
| WFS 2.0 | `<ows:WGS84BoundingBox>`, `<DefaultCRS>`, `<OtherCRS>[]` |
| WCS 2.0 | `<gml:boundedBy><gml:Envelope srsName="…">` |
| Esri FeatureServer | `spatialReference.wkid` / `latestWkid`; `extent` with `xmin/ymin/xmax/ymax/spatialReference`; per-layer `extent` |
| Esri MapServer | `spatialReference`, `initialExtent`, `fullExtent` (both) |
| Esri ImageServer | `spatialReference`, `extent`, `pixelType`, `pixelSizeX`, `pixelSizeY` |
| STAC | `collection.extent.spatial.bbox[]` (multi-polygon supported), `crs` extension |
| OData | spatial type metadata via Edm.GeographyPoint etc.; SRID annotation |
| GeoJSON | implicit CRS84 (longitude/latitude), optional `bbox` at FC level |
| GeoParquet | `geo.columns.<col>.crs` (PROJJSON), `geo.columns.<col>.geometry_types`, `geo.columns.<col>.bbox` |
| FlatGeobuf | header `crs.wkt`/`crs.org`/`crs.code`, `geometry_type`, header `envelope` |
| GeoPackage | `gpkg_spatial_ref_sys` table, `gpkg_geometry_columns.srs_id` and `geometry_type_name` |
| KML | implicit CRS84; `<Region><LatLonAltBox>` |
| GML | `srsName` attribute on every geometry |
| Shapefile | `.prj` file (WKT) |
| COG | EPSG code in GeoTIFF tags |
| Zarr / NetCDF | CF conventions: `grid_mapping_name`, `crs` variable |

**Verdict**: ⚠️ Single CRS + bbox + geometry type covered. **Multi-CRS** (OGC API Features Part 2) is a real gap — many clients ask for collections that can serve features in multiple CRS. **StorageCrs ≠ ResponseCrs** is also a gap when our backend reprojects. Add the three missing fields per my prior slice C.

---

## 7. Temporal extent & time fields

| V2 canonical | Status |
|---|---|
| `Resource.Temporal.StartTimeField` | ✅ |
| `Resource.Temporal.EndTimeField` | ✅ |
| `Resource.Temporal.TrackIdField` | ✅ |
| `Resource.Temporal.Extent` ({Start, End}) | ✅ |
| `Resource.Temporal.TimeReference` (timezone) | ❌ — gap (Esri `dateFieldsTimeReference`) |
| `Resource.Temporal.HasLiveData` (Esri) | ❌ — leaking through Extensions if needed |
| Multi-interval extent | ❌ — only single `Extent` |

| Standard | Maps to |
|---|---|
| OGC API - Features | `extent.temporal.interval[]` (multi-interval supported), `extent.temporal.trs` |
| OGC API - Records | `record.time`, `record.created`, `record.published` |
| WMS | `<Dimension name="time" units="ISO8601">` |
| WMTS | `<Dimension>` (TIME) |
| WFS 2.0 | filter encoding for temporal predicates |
| Esri FeatureServer | `timeInfo.{startTimeField, endTimeField, trackIdField, timeExtent, timeReference, timeInterval, hasLiveData}` |
| Esri MapServer | `timeInfo` (same) per-layer |
| STAC | `properties.datetime` (Item), `extent.temporal.interval[][]` (Collection) — supports nulls for open ranges |
| OData | Edm.DateTimeOffset properties |
| GeoJSON | non-standard; convention `properties.datetime` |
| FlatGeobuf | none (geometry-only header) |
| GeoPackage | metadata extension only |
| KML | `<TimeStamp>` or `<TimeSpan>` |
| NetCDF | CF time variable with `units`, `calendar`, `time_origin` |

**Verdict**: ⚠️ Covered for the common case. Gaps: **TimeReference** (timezone hint for Esri/CF compliance), **multi-interval extents** (STAC + OGC API Features both support; we have single-interval), **HasLiveData** (Esri flag for streaming time-aware data). Add to `MetadataV2ResourceTemporal` as optional fields.

---

## 8. Schema fields (per-attribute)

| V2 canonical | Status |
|---|---|
| `Field.Name` | ✅ |
| `Field.Type` (enum) | ✅ |
| `Field.Title` | ✅ |
| `Field.Description` | ✅ |
| `Field.Nullable` | ✅ |
| `Field.SemanticRoles[]` (id.primary, geometry.primary, …) | ✅ |
| `Field.Alias` | ❌ — gap (Esri field aliases) |
| `Field.Editable` | ❌ — gap |
| `Field.Length` (for VARCHAR-typed) | ❌ — gap |
| `Field.DefaultValue` | ❌ — gap |
| `Field.Domain` (coded values / range) | ❌ — gap |
| `Field.SqlType` (provider-native type) | ❌ — leaking through `Extensions` |
| `Field.GlobalIdField` flag | ❌ — Editing slot has it (proposed) |
| `Field.Extensions` (open bag) | ✅ |

| Standard | Maps to |
|---|---|
| OGC API - Features Part 5 (Schemas) | JSON Schema document at `/collections/{id}/schema`; field properties: `title`, `description`, `type`, `format`, `enum` (← Domain.codedValues), `minimum`/`maximum` (← Domain.range), `default` (← DefaultValue), `nullable`, custom `x-ogc-role` (← SemanticRoles) |
| OGC API - Features Part 3 (Filter) | `/collections/{id}/queryables` lists filter-able properties with JSON Schema |
| WFS 2.0 DescribeFeatureType | xsd:schema with xsd:element per field (name + type + nullable) |
| Esri FeatureServer fields | `name`, `type` (esriFieldType*), `alias`, `sqlType`, `nullable`, `editable`, `length`, `defaultValue`, `domain` (codedValue or range with `inheritedFrom`), `description` |
| Esri MapServer fields | same as FeatureServer |
| STAC | `properties.*` arbitrary; STAC extensions add typed shapes (eo:bands, sar:polarizations, view:angles) |
| OData | `<Property Name="…" Type="Edm.X" Nullable="…" DefaultValue="…" MaxLength="…">` |
| GeoJSON | `properties.*` arbitrary, untyped at the spec level |
| GeoParquet | Parquet schema with logical types; column-level metadata |
| FlatGeobuf | header `columns[]` with `name`, `type`, `nullable`, `title`, `description` |
| GeoPackage | `gpkg_data_columns` (logical type metadata), `gpkg_data_column_constraints` (= Domain), `gpkg_extensions` |
| KML | `<ExtendedData><SchemaData>` |
| GML | xsd:schema in app schema |
| Shapefile | `.dbf` header (name, type, length, decimal-count) |

**Verdict**: ❌ Field-level metadata is the biggest current gap. Esri FeatureServer fields[] needs `alias`, `editable`, `length`, `defaultValue`, `domain` to round-trip. OData EDM needs `MaxLength`. GeoPackage `gpkg_data_column_constraints` maps directly to `Domain`. **Field extensions are slice A from my prior reply — necessary for parity.**

---

## 9. Display / render hints

| V2 canonical | Status |
|---|---|
| `Resource.Display.MinScale` / `MaxScale` | ❌ — proposed, not landed |
| `Resource.Display.DefaultVisibility` | ❌ — proposed |
| `Resource.Display.DisplayField` | ❌ — proposed |
| `Resource.Display.Queryable` | ❌ — proposed |
| `Resource.Display.Opaque` (WMS) | ❌ — proposed |
| `Resource.Display.HasZ` / `HasM` | ❌ — proposed |
| `Resource.Display.HtmlPopupType` (Esri) | ⚠️ — would live in `Extensions["esri-popup"]` |
| `Resource.Display.CanScaleSymbols` | ❌ — proposed (cap flag) |

| Standard | Maps to |
|---|---|
| OGC API - Features | n/a (no display hints in spec) |
| OGC API - Maps | `minScaleDenominator` / `maxScaleDenominator` per collection |
| OGC API - Tiles | min/max zoom from tile matrix set rather than scale |
| OGC API - Styles | scale ranges live inside the style body, not on the resource |
| WMS | `<MinScaleDenominator>`, `<MaxScaleDenominator>` per layer; `queryable="1"`; `opaque="1"`; `noSubsets`, `fixedWidth`, `fixedHeight`; per-style scale denominators |
| WMTS | scale via TileMatrix `ScaleDenominator`; visibility implicit |
| Esri FeatureServer | `minScale`, `maxScale`, `defaultVisibility`, `displayField`, `htmlPopupType`, `canModifyLayer`, `canScaleSymbols`, `hasLabels`, `hasZ`, `hasM` |
| Esri MapServer | same per-layer |
| STAC | `properties.proj:epsg` per item; thumbnail asset link |
| OData | n/a |

**Verdict**: ❌ Entire `Resource.Display` slot is a gap — needed by every map/tile protocol. **Land slice E from prior reply.**

---

## 10. Filtering (permanent / saved filter)

| V2 canonical | Status |
|---|---|
| `Resource.PermanentFilter.Expression` | ✅ |
| `Resource.PermanentFilter.Language` (arcgis-sql / cql2-text / cql2-json) | ✅ |

| Standard | Maps to |
|---|---|
| OGC API - Features Part 3 | `/collections/{id}` doesn't expose; permanently applied server-side. Conformance class `https://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter` |
| WFS 2.0 | server-side; not advertised in capabilities |
| Esri FeatureServer | `definitionExpression` per layer (visible in layer metadata) |
| Esri MapServer | `definitionExpression` per layer |
| STAC | server-side filtering of items; not advertised |
| OData | applied as `$filter` prefix server-side |

**Verdict**: ✅ Covered. PermanentFilter is a clean single concept across standards.

---

## 11. Relationships

| V2 canonical | Status |
|---|---|
| `Resource.Relationships[]` | ✅ |
| `Relationship.Id` | ✅ |
| `Relationship.Name`, `Description` | ✅ |
| `Relationship.RelatedResourceId` | ✅ |
| `Relationship.Role` | ✅ |
| `Relationship.Cardinality` | ✅ |
| `Relationship.OriginField` | ✅ |
| `Relationship.DestinationField` | ✅ |
| `Relationship.EsriRelationshipId` (int) | ✅ |

| Standard | Maps to |
|---|---|
| OGC API - Features | non-standard; STAC `links rel=related` or extension `links rel=child` |
| Esri FeatureServer | `relationships[]` per layer + service-level `relationships[]` registry. Fields: `id` (int), `name`, `relatedTableId`, `role` (origin/destination), `keyField`, `cardinality`, `composite` |
| Esri MapServer | same |
| WFS 2.0 | join queries via `<wfs:Join>` extension |
| OData | `<NavigationProperty>` between EntityTypes; `$expand` |
| STAC | `links rel=parent`, `links rel=collection`, `links rel=item`, `links rel=related` |
| GeoPackage | `gpkg_extensions` with `gpkg_related_tables` extension (RTE) |

**Verdict**: ✅ Covered. The OData `NavigationProperty` and Esri `relatedTableId/role/keyField` map cleanly onto our `RelatedResourceId/Role/OriginField/DestinationField`.

---

## 12. Storage binding & access

| V2 canonical | Status |
|---|---|
| `Resource.StorageBindingIds[]` ([0] = primary) | ✅ |
| `StorageBinding.ResourceId` | ✅ |
| `StorageBinding.ConnectionId` | ✅ |
| `StorageBinding.StorageType` (enum) | ✅ |
| `StorageBinding.Locator` (table/object key/URI) | ✅ |
| `StorageBinding.StorageLayerId` (int) | ✅ |
| `StorageBinding.Capabilities[]` (enum) | ✅ |
| `Connection.Type` (enum) | ✅ |
| `Connection.Provider` (postgres / s3 / stac / honua) | ✅ |
| `Connection.Endpoint` (Uri?) | ✅ |
| `Connection.SecretRef` | ✅ |

| Standard | Maps to |
|---|---|
| OGC API - * | n/a — not exposed externally |
| Esri | n/a — not exposed externally |
| STAC | `assets.*` (item-level), `assets[].href`, `assets[].type`, `assets[].roles[]` — **closest match** for storage-as-asset |
| GeoPackage | `gpkg_contents` row per table; data lives in the same file |
| MBTiles / PMTiles | metadata table at file level |
| COG | TIFF tags in same file |

**Verdict**: ✅ Internal-only concept (admin / runtime). Not exposed in any of the catalog standards. STAC `assets` is the closest external analog and that's handled via `Publication`-shaped projection, not StorageBinding.

---

## 13. Styling

| V2 canonical | Status |
|---|---|
| `Resource.StyleResourceIds[]` ([0] = primary) | ❌ — deleted in slice 66/N, **must re-add** |
| `Resource.Style` (on `Type=Style` resources) | ❌ — proposed, not landed |
| `Style.Encodings[]` (mapbox / sld-1.0.0 / sld-1.1.0 / esri-drawing-info / esri-image-renderer / 3d-tiles-styling) | ❌ — proposed |
| `Style.StyleVersion` (cache key) | ❌ — currently in `LayerStyleDefinition` (v1) |
| `Style.Title` / `Abstract` / `LegendUrl` | ❌ — proposed |

| Standard | Maps to |
|---|---|
| OGC API - Styles | `style.id`, `style.title`, `style.description`, `style.scope`, `style.links[]` (rel=stylesheet for each encoding), one body per encoding |
| OGC API - Maps | optional `styleId` query parameter |
| WMS | `<Style><Name>`, `<Title>`, `<Abstract>`, `<LegendURL>` per layer; SLD via `?SLD_BODY=` or named via `?STYLES=` |
| WMTS | `<Style>` per layer with `<ows:Identifier>`, `<ows:Title>`, `<LegendURL>`, `isDefault` |
| Esri FeatureServer | `drawingInfo` (`renderer`, `labelingInfo`, `transparency`, `scaleSymbols`) per layer |
| Esri MapServer | `drawingInfo` per layer |
| Esri ImageServer | `defaultRenderingRule`, `mosaicRule`, `serviceDataType` |
| STAC | `assets[]` with `roles=["overview"]` for legend thumbnails |
| MBTiles | `metadata` table with `vector_layers` array |
| PMTiles | `tilejson` header with `vector_layers` |

**Verdict**: ❌ Style model is the biggest current gap. Multiple encodings + version + legend URL all need typed slots. **Land styles slice from prior reply.**

---

## 14. Capabilities & supported operations

| V2 canonical | Status |
|---|---|
| `Service.Protocols[]` (single source of truth) | ✅ |
| `Resource.Editing.CanModify` (proposed) | ❌ — proposed |
| `Resource.Editing.SupportsAttachments` (proposed) | ❌ — proposed |
| `Resource.Editing.SupportsRelatedRecords` (proposed) | ❌ — proposed |
| `Resource.Editing.HasLabels` (proposed) | ❌ — proposed |
| `StorageBinding.Capabilities[]` (Query/Filter/Sort/Aggregate/Edit/Transactions/Render/Tile/Download/Search) | ✅ |
| `Publication.Capabilities[]` (deleted slice 68/N — was unused string list) | ⊘ |

| Standard | Maps to |
|---|---|
| OGC API - Features | `conformance` document lists conformance classes (cores, CRS, Filter, Editing, …) — derived from `Service.Protocols` + extension flags |
| OGC API - Common | `/conformance` endpoint mandatory |
| WMS GetCapabilities | `<Request>` operations list, `<Layer queryable="…" opaque="…">` flags |
| WMTS | `<Operations>` |
| WFS 2.0 | `<ows:OperationsMetadata>` with `<Operation>` entries |
| WCS 2.0 | `<ows:OperationsMetadata>` |
| Esri FeatureServer | `capabilities` (csv string: `"Create,Delete,Query,Update,Editing,Sync"`); per-layer `capabilities` |
| Esri MapServer | `capabilities` (csv) |
| Esri ImageServer | `capabilities` (`"Image,Catalog,Metadata,Download,Pixels"`) |
| STAC | `links[]` with `rel=search`, `rel=conformance`; `conformsTo[]` |
| OData | `$metadata` document with `Capabilities.*` annotations |

**Verdict**: ⚠️ Service-level protocols covered. Resource-level editing capabilities (per-layer `canModify`, attachments, related records, labels) are gaps — needed by both Esri layer metadata and OGC API Features Part 4 (Editing). **Land Editing slot.**

---

## 15. Editing tracking

| V2 canonical | Status |
|---|---|
| `Resource.Editing.GlobalIdField` (proposed) | ❌ |
| `Resource.Editing.CreatorField` | ❌ |
| `Resource.Editing.CreatedAtField` | ❌ |
| `Resource.Editing.EditorField` | ❌ |
| `Resource.Editing.UpdatedAtField` | ❌ |

| Standard | Maps to |
|---|---|
| Esri FeatureServer | `editorTrackingInfo.{enableEditorTracking, creatorField, createdAtField, editorField, editedAtField, enableOwnershipAccessControl, allowOthersToUpdate, allowOthersToDelete}`; `globalIdField` |
| OGC API - Features Part 4 | optional headers `Created-By`, `Modified-By` on item PATCH/PUT; field-level tracking is implementation-defined |
| OData | `Org.OData.Capabilities.V1.ChangeTrackingRetention` annotation; `$audit` extensions |
| GeoPackage | metadata extension; not core |
| STAC | `properties.created`, `properties.updated` at item level |

**Verdict**: ❌ Major Esri feature with no V2 home. Land the Editing slot.

---

## 16. Service-level operational settings

| V2 canonical | Status |
|---|---|
| `Service.Settings.MaxRecordCount` (proposed) | ❌ |
| `Service.Settings.DefaultRecordCount` | ❌ |
| `Service.Settings.MaxImageWidth/Height` | ❌ |
| `Service.Settings.DefaultDpi` | ❌ |
| `Service.Settings.MaxFeaturesPerLayer` | ❌ |
| `Service.Settings.DefaultFormat` | ❌ |
| `Service.Settings.SupportedQueryFormats[]` | ❌ |
| `Service.Settings.SupportedExportFormats[]` | ❌ |
| `Service.Settings.DefaultTileMatrixSet` | ❌ |
| `Service.Settings.SyncEnabled` | ❌ |

| Standard | Maps to |
|---|---|
| OGC API - Features | declared per collection: `maxItems`, `default Limit`, supported `f=` formats |
| OGC API - Maps | image dimension limits, supported pixel formats |
| OGC API - Tiles | tile matrix set, formats |
| WMS | `<MaxWidth>`, `<MaxHeight>`, format list in `<Request>` |
| WMTS | format list per layer |
| WFS 2.0 | `<wfs:Constraint name="CountDefault" value="…">` |
| Esri FeatureServer | `maxRecordCount`, `maxRecordCountFactor`, `supportedQueryFormats`, `supportedExportFormats`, `syncEnabled`, `syncCapabilities` |
| Esri MapServer | `maxImageWidth`, `maxImageHeight`, `maxRecordCount`, `supportedImageFormatTypes` |
| Esri ImageServer | `maxImageWidth`, `maxImageHeight`, `maxDownloadImageCount`, `allowedCompressions` |
| STAC | `links[].method`, `links[].body` define server constraints implicitly |
| OData | `Org.OData.Capabilities.V1.TopSupported`, `MaxResults` annotations |

**Verdict**: ❌ Currently leaking through `Service.Options` JsonElement bag. Land `Service.Settings` typed slot.

---

## 17. Links

| V2 canonical | Status |
|---|---|
| `Metadata.Links[]` (typed list of `{href, rel, type, title, hreflang}`) | ❌ |

| Standard | Maps to |
|---|---|
| OGC API - Common | `links[]` **mandatory** on every resource. Rels: `self`, `alternate`, `data`, `items`, `queryables`, `schema`, `conformance`, `license`, `enclosure`, `cite-as`, `describedby` |
| OGC API - Features | same |
| OGC API - Records | `links[]` mandatory; rels above + `via`, `child`, `parent`, `related` |
| OGC API - Styles | `links[]` with `rel=stylesheet` (one per encoding) |
| Esri | `Esri` REST is JSON-only, no link rels; but `/info` endpoint references parent service |
| STAC | `links[]` mandatory. Rels: `self`, `parent`, `root`, `collection`, `item`, `items`, `child`, `data`, `search`, `derived_from`, `via`, `cite-as`, `next`/`prev` (pagination), `service-doc`, `service-desc`, `license`, `canonical` |
| OData | `@odata.context`, `@odata.nextLink`, `@odata.deltaLink`, `<Annotation Term="Org.OData.Core.V1.Links">` |
| GeoJSON | non-standard; sometimes `properties.@links` |
| KML | `<Link>` for network links |

**Verdict**: ❌ Massive gap. Every catalog standard requires `links[]`. Currently no typed home. Land `Metadata.Links[]` (slice from prior reply).

---

## 18. Tiling-specific

| V2 canonical | Status |
|---|---|
| `Service.Settings.DefaultTileMatrixSet` | ❌ — proposed |
| Per-publication tile matrix set link | ❌ |
| Tile schemes per publication | ❌ |
| Min/Max zoom per publication | ⚠️ — derivable from `Display.MinScale/MaxScale` |

| Standard | Maps to |
|---|---|
| OGC API - Tiles | `tileMatrixSetLinks[]`, `tileMatrixSetURI`, per-collection min/max zoom |
| WMTS | `<TileMatrixSet>` (full geometry of every zoom level), `<TileMatrixSetLink>`, `<TileMatrixLimits>` |
| Esri MapServer (cached) | `tileInfo.{rows, cols, dpi, format, compressionQuality, origin, spatialReference, lods[]}` |
| MBTiles | `metadata` table: `name`, `format`, `minzoom`, `maxzoom`, `bounds`, `center`, `attribution` |
| PMTiles | header: `tile_compression`, `tile_type`, `min_zoom`, `max_zoom`, `min_lon`, `max_lon`, `min_lat`, `max_lat`, `center_zoom` |
| TileJSON (Mapbox) | top-level: `tilejson`, `tiles[]`, `minzoom`, `maxzoom`, `bounds`, `center`, `vector_layers[]` |

**Verdict**: ⚠️ Partial. For services that publish to MBTiles/PMTiles, we'd want explicit min/max zoom + tile matrix set per publication. Either:
- Derive from `Display.MinScale/MaxScale` + a standard scale-to-zoom conversion (clean but lossy on non-standard tile schemes).
- Add `Publication.Tiling: MetadataV2PublicationTiling` for tile-specific publications.

Recommend the latter only when a real consumer needs the non-standard tile scheme support.

---

## 19. Raster / coverage-specific

| V2 canonical | Status |
|---|---|
| `Resource.Spatial.GeometryType = None` + raster type | ✅ — currently signaled by `Resource.Type = RasterDataset` |
| Band metadata | ❌ |
| No-data values | ❌ |
| Stretch / colormap defaults | ⚠️ — in style |
| Pixel type, size | ❌ |
| Compression info | ❌ |
| Mosaic rules (Esri) | ⚠️ — in `Resource.Extensions["rasterMosaic"]` per ImageServer port |

| Standard | Maps to |
|---|---|
| OGC API - Coverages | `coverage.coverageRangeType`, `coverage.domainSet`, `coverage.metadata` |
| WCS 2.0 | `<gmlcov:rangeType>` with `<swe:DataRecord>` describing bands; `<gmlcov:metadata>`; `<wcs:CoverageId>` |
| Esri ImageServer | `pixelType`, `bandCount`, `serviceDataType`, `noData`, `defaultMosaicMethod`, `allowedMosaicMethods[]`, `bandNames[]`, `histograms`, `statistics` |
| STAC | `assets[].roles=["data"]`, plus `eo` extension for bands, `raster` extension for sample type, `proj` extension for grid |
| COG | TIFF tags: `BitsPerSample`, `SampleFormat`, `PhotometricInterpretation`, `NoDataValue`, `ColorMap` |
| Zarr | `zarr.json` with `data_type`, `fill_value`, `chunks`, `codec` |
| NetCDF | `_FillValue`, `add_offset`, `scale_factor`, `units`, `long_name` per variable |
| GeoTIFF | as COG above |

**Verdict**: ❌ Big gap for raster/coverage publishing. Need typed `Resource.Raster` slot with band metadata + no-data + pixel type. Defer until raster cluster handler ports demand it (currently in ImageServer / WCS / OGC API Coverages partial ports).

---

## 20. STAC-specific extensions

| V2 canonical | Status |
|---|---|
| `Resource.Extensions["stac"]` (free-form bag) | ⚠️ |

| STAC extension | Currently |
|---|---|
| `eo` (electro-optical bands, cloud cover) | `Extensions["stac"]["eo"]` |
| `sar` (synthetic aperture radar) | `Extensions["stac"]["sar"]` |
| `view` (sun/satellite geometry) | `Extensions["stac"]["view"]` |
| `proj` (per-item projection) | `Extensions["stac"]["proj"]` |
| `raster` (sample stats) | `Extensions["stac"]["raster"]` |
| `processing` | `Extensions["stac"]["processing"]` |
| `version` | `Extensions["stac"]["version"]` |
| `mlm` (machine-learning model) | `Extensions["stac"]["mlm"]` |
| `web-map-links` | `Extensions["stac"]["web-map-links"]` |

**Verdict**: ⚠️ STAC extensions are intentionally an open vocabulary on a fast-moving spec. Keeping these in `Extensions["stac"][<ext>]` is the right call — typing each STAC extension would chase a moving target. Document the convention in `MetadataV2Resource.Extensions` doc-comment.

---

## 21. Package-format specifics

### GeoJSON (RFC 7946)
- Feature: `id`, `type`, `geometry`, `properties`, `bbox?`
- FeatureCollection: `type`, `features[]`, `bbox?`
- Implicit CRS84
- ✅ V2 covers all required mappings; no GeoJSON-specific metadata gap

### GeoParquet (file-level metadata)
- `geo.version`, `geo.primary_column`, `geo.columns.{name}.{encoding, geometry_types, crs, edges, orientation, bbox, epoch}`
- ✅ Maps to `Spatial.SpatialReference/Bbox/GeometryType` + `PrimaryGeometryField`
- ⚠️ `geo.columns.{name}.epoch` maps to gap **StorageCrsCoordinateEpoch**

### FlatGeobuf (header)
- `name`, `envelope[]`, `geometry_type`, `has_z`, `has_m`, `crs.{org, code, name, wkt}`, `columns[]`
- ✅ Maps to existing V2 + proposed `Display.HasZ/HasM`

### GeoPackage
- `gpkg_contents`, `gpkg_geometry_columns`, `gpkg_spatial_ref_sys`, `gpkg_data_columns`, `gpkg_data_column_constraints`, `gpkg_extensions`, `gpkg_metadata`
- ⚠️ `gpkg_data_column_constraints` maps to gap **Field.Domain**
- ⚠️ `gpkg_metadata` (XML metadata sidecar) overlaps with `Resource.Extensions`

### KML
- `<Document>{name, description, atom:author, atom:link, ExtendedData}`
- ✅ Maps to existing V2

### GML 3.2.1
- `gml:name`, `gml:description`, `gml:metaDataProperty[]`, `gml:boundedBy`, app-schema `xsd:schema`
- ⚠️ `gml:metaDataProperty` for arbitrary nested metadata maps to `Extensions`

### Shapefile
- `.dbf` field defs (name, type, length, decimals) + `.prj` (WKT) + `.shp.xml` (FGDC/ISO 19139 metadata)
- ⚠️ `.dbf` length/decimals map to gap **Field.Length**
- ⚠️ `.shp.xml` metadata sidecar overlaps with `Resource.Extensions["fgdc"]` or similar

### MBTiles / PMTiles
- See §18 (Tiling)

### Cloud-Optimized GeoTIFF (COG)
- See §19 (Raster)

### Zarr / NetCDF
- See §19 (Raster) + CF conventions

---

## Summary of identified gaps

Ranked by impact (most → least):

### Critical (blocks correct response in major catalog standards)
1. **`Metadata.Links[]`** — required by every OGC API spec
2. **`Metadata.License`** + **`Metadata.Attribution`** — required by STAC and OGC API Records
3. **`Field.Alias` / `Field.Editable` / `Field.Length` / `Field.DefaultValue` / `Field.Domain`** — Esri FeatureServer parity
4. **`Resource.Editing.*`** (GlobalIdField, EditorTracking, capability flags) — Esri parity + OGC API Features Part 4
5. **`Resource.Display.*`** (MinScale/MaxScale/DefaultVisibility/DisplayField/Queryable/Opaque/HasZ/HasM) — required by every Map/Tile protocol
6. **`Service.Settings.*`** — required by every protocol's capability/limit reporting
7. **`Style.Encodings[]`** model (re-add deleted `Resource.StyleResourceIds`)

### Important (parity for full catalog projection)
8. **`Resource.Spatial.SupportedCrs[]` / `StorageCrs` / `StorageCrsCoordinateEpoch`** — OGC API Features Part 2 + GeoParquet epoch
9. **`Metadata.Keywords` / `Themes` / `Language` / `ContactPoint` / `Publisher`** — catalog facets
10. **`Resource.Temporal` extensions** — multi-interval, TimeReference (timezone), HasLiveData
11. **`Resource.Raster`** slot — band info, no-data, pixel type (only when Raster handlers port)

### Minor (niche or already in Extensions)
12. **`Resource.Metadata.Generation`** — re-add (deleted in slice 65/N); needed by OData ETags + admin if-match
13. **`Esri templates / subtypes / htmlPopupType`** — keep in `Extensions["esri-*"]`
14. **`STAC extensions`** — keep in `Extensions["stac"]`
15. **`indexes[]`** — informational; keep in `Extensions` if surfaced
16. **`Tiling per-publication metadata`** — only when MBTiles/PMTiles publishing needs non-standard tile schemes

## Next-slice landing order

Recommend bundling the typed slot additions into 4 commits:

```
Slice 1: Metadata block universals
  - Metadata.Keywords, Themes, Language, ContactPoint, Publisher, License, Attribution, Links
  - Promote from Resource.Extensions["stac"] (license/attribution) onto Metadata
  - Update STAC port to read from Metadata.* instead of Extensions

Slice 2: Field-level extensions
  - Field.Alias, Editable, Length, DefaultValue, Domain (coded/range)
  - Field.SqlType (provider-native label)
  - Update FeatureServerUtilities MapFieldInfoV2 / OData $metadata generator

Slice 3: Resource render + edit slots
  - Resource.Display (MinScale/MaxScale/DefaultVisibility/DisplayField/Queryable/Opaque/HasZ/HasM/CanScaleSymbols)
  - Resource.Editing (GlobalIdField + EditorTrackingFields + CanModify + SupportsAttachments + SupportsRelatedRecords + HasLabels)

Slice 4: Service settings + Spatial multi-CRS
  - Service.Settings (Max*/Default*/Supported*)
  - Resource.Spatial.SupportedCrs[] / StorageCrs / StorageCrsCoordinateEpoch

Slice 5: Style resource model
  - Resource.StyleResourceIds (re-add)
  - Resource.Style + MetadataV2StyleEncoding
  - Graph index ResourcesByStyleResourceId

Slice 6 (deferred): Resource.Raster
  - Lands when raster handler ports start consuming it
```

After these slices, every concept above can be answered from typed V2 slots — no per-protocol "patch the response with Extensions" code.
