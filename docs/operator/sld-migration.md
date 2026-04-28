# SLD Migration Reference

GeoServer's styling system is SLD/SE XML. Honua's canonical style format is MapLibre GL Style JSON. The Admin SLD endpoints provide a best-effort, server-side conversion path so migration projects can preserve the bulk of their layer symbology without rewriting hundreds of style files by hand.

This reference catalogs the supported SLD subset, the diagnostic taxonomy, the security stance, and the known limitations callers must plan for. It complements [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md).

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/admin/metadata/layers/{layerId:int}/style/import-sld` | Convert raw SLD/SE XML and persist the resulting MapLibre layers as the layer's stored style. |
| `GET` | `/api/v1/admin/metadata/layers/{layerId:int}/style/export-sld` | Render the stored MapLibre style as SLD 1.0 XML for round-trip validation or downstream use. |

Both endpoints require admin authorization. They are available in Community edition; no `FeatureCatalog` gate is applied.

### Import request

```http
POST /api/v1/admin/metadata/layers/0/style/import-sld
Authorization: ApiKey honua-admin
Content-Type: application/xml

<?xml version="1.0" ...?>
<StyledLayerDescriptor version="1.0.0" xmlns="http://www.opengis.net/sld" ...>
  ...
</StyledLayerDescriptor>
```

Successful (200) response shape:

```json
{
  "success": true,
  "data": {
    "detectedVersion": "Sld10",
    "layerCount": 1,
    "mapLibreStyle": { "version": 8, "sources": { ... }, "layers": [ ... ] },
    "diagnostics": [
      { "severity": "Warning", "construct": "ExternalGraphic", "message": "...", "ruleName": "icon-rule" }
    ]
  },
  ...
}
```

When error-severity diagnostics block import, the API returns 422 with a structured failure envelope:

```json
{
  "success": false,
  "message": "SLD import failed; see diagnostics.",
  "data": {
    "detectedVersion": "Sld10",
    "diagnostics": [
      { "severity": "Error", "construct": "MapLibreLayers", "message": "SLD document contained no convertible symbolizers." }
    ]
  }
}
```

No partial stylesheet is stored. The diagnostic count is also recorded in the server log via the `SldImportRejected` structured log entry. Malformed or unsafe XML returns 400 with a generic problem detail; raw exception messages are never echoed. Payloads larger than the 1 MiB cap return 413 before parsing.

### Export response

A 200 response is `application/xml` containing a complete SLD 1.0 document. The `X-Sld-Diagnostic-Count` header reports the number of diagnostics emitted while exporting; the `X-Sld-Diagnostics` header carries the JSON-encoded diagnostic array when the count is non-zero. If the stored MapLibre style cannot be exported (no convertible layers, deserialization failure, or all layers produced error diagnostics), the endpoint returns 422 with the same failure envelope as the import path — `success: false` plus a `data.diagnostics` array describing why the export was refused. Layer routing returns 404 for missing layers and 400 for invalid layer identifiers (matching the rest of the admin layer endpoints).

## Supported subset

| SLD construct | MapLibre layer type | Notes |
|---|---|---|
| `PointSymbolizer` / `Mark` (any well-known name) | `circle` | Non-`circle` well-known names emit a `Mark.WellKnownName` warning; sprites are not generated. `Stroke` / `stroke-opacity` round-trip via `circle-stroke-color` and `circle-stroke-opacity` as separate paint properties. |
| `PointSymbolizer` / `ExternalGraphic` | `symbol` | `icon-image` is set to the resource href. Remote URIs are recorded but never fetched. SLD `<Size>` is in absolute pixels, MapLibre `icon-size` is a scale factor; the converter emits a `Graphic.Size` warning and omits `icon-size` rather than mis-scale the sprite (provide sprite metadata to set the scale factor). |
| `LineSymbolizer` (`stroke`, `stroke-width`, `stroke-opacity`, `stroke-dasharray`, `stroke-linecap`, `stroke-linejoin`) | `line` | `PerpendicularOffset` ignored with warning. |
| `PolygonSymbolizer` (`Fill` and/or `Stroke`) | `fill` for the body and a separate `line` for the outline | A `fill` layer is emitted only when the SLD has a `Fill`; otherwise the polygon is exported as a single `line` layer. Outline always lives on the dedicated `line` layer (no `fill-outline-color`) to avoid double-stroking. SLD/SE default `stroke-width` of `1.0` is applied when omitted. `Displacement` ignored with warning. |
| `TextSymbolizer` (`Label`, `Font`, `Fill`, `Halo`) | `symbol` | Only `<ogc:PropertyName>` labels are mapped to `{field}`. Functions warn and the label is dropped. |
| `MinScaleDenominator` / `MaxScaleDenominator` | `minzoom` / `maxzoom` | Web Mercator approximation: `zoom ≈ log2(559082264 / scale)`, clamped to `[0,24]`. Latitude variance is documented in the design brief. |
| OGC Filter `PropertyIsEqualTo`, `PropertyIsNotEqualTo`, `PropertyIsLessThan*`, `PropertyIsGreaterThan*` | MapLibre comparison expressions | `PropertyIsBetween` decomposes into `>= AND <=` when feasible. |
| `And`, `Or`, `Not` | `["all", ...]`, `["any", ...]`, `["!", ...]` | If any child operand is unsupported, the entire compound filter is dropped (rule renders unfiltered) so `And`/`Or` semantics are not silently narrowed or broadened. |

## Unsupported and lossy constructs

The converter never silently drops these — each emits a `Warning` diagnostic, and the surrounding rule is preserved (rendering unfiltered or with the offending property omitted):

- `VendorOption`, GeoServer-specific extensions
- `RasterSymbolizer` (raster styling is handled by Honua's raster pipeline separately)
- `ExternalGraphic` with a remote URI (no remote resource is fetched; sprite must be supplied separately)
- OGC `Function` expressions in filters and labels
- Spatial/temporal predicates: `BBOX`, `Intersects`, `Contains`, `Within`, `Beyond`, `DWithin`, `After`, `Before`, `During`, etc.
- `PropertyIsLike` — SLD wildcard semantics (`%`, `_`, configurable via `wildCard`/`singleChar`) have no portable MapLibre filter equivalent; the rule renders unfiltered
- `PropertyIsNull`
- `ElseFilter`
- `LabelPlacement` (only basic placement defaults are honored)
- `Graphic.Rotation` (no `icon-rotate` mapping yet)
- `Graphic.Size` on `ExternalGraphic` — SLD Size is absolute pixels but MapLibre `icon-size` is a scale factor; without sprite intrinsic dimensions the conversion is lossy and `icon-size` is omitted with a warning. Mirror behavior on export: a MapLibre `icon-size` literal warns and is dropped from the SLD `<Graphic>`
- `Transformation` on `FeatureTypeStyle`
- `UserLayer`, `NamedStyle` (server-side style references)

## Color handling

- CSS named colors and `#RRGGBB` are passed through.
- `#AARRGGBB` (alpha-prefixed) is normalized to `rgba(R,G,B,A)`.
- `<Opacity>` and `fill-opacity` / `stroke-opacity` CSS parameters are emitted as a separate `*-opacity` paint property (`circle-opacity`, `circle-stroke-opacity`, `line-opacity`, `fill-opacity`, `text-opacity`) so MapLibre does not multiply the alpha twice. The exception is text halo, where MapLibre has no `text-halo-opacity` paint property and the opacity must ride inside `text-halo-color` via `rgba()`. `TextSymbolizer` `<Fill>` opacity round-trips through `text-opacity` on both import and export, matching the rest of the supported subset.

## MapLibre → SLD export limitations

Round-trip fidelity is intentionally limited to the supported subset. Specifically:

- Data-driven MapLibre expressions (`match`, `step`, `interpolate`, `case`) cannot be expressed as plain SLD 1.0 and emit a warning per property; the offending property is omitted from the output.
- MapLibre filter operators outside the `==`, `!=`, `<`, `<=`, `>`, `>=`, `all`, `any`, `!` set are dropped from the exported `<ogc:Filter>`.
- Background layers (`type: "background"`) have no SLD equivalent and are skipped with a warning.
- The exported SLD always uses the SLD 1.0 namespace (`http://www.opengis.net/sld`); SE 1.1 export is not implemented.

For higher-fidelity export (vendor function emission, GeoServer extension preservation), the Pro/Enterprise migration tooling is the appropriate path.

## Security stance

- All XML parsing routes through `SecureXmlDocumentParser`, which sets `DtdProcessing.Prohibit`, `XmlResolver = null`, and `MaxCharactersFromEntities = 0`. DTD subsets and external entities are rejected as parse errors.
- Inputs are capped at 1 MiB to bound conversion cost. Larger uploads return 413.
- Remote `ExternalGraphic` URIs are never dereferenced by the server; conversion is purely metadata-only.
- Raw exception messages, file paths, and entity contents are never echoed in responses. Only structured diagnostics surface.

## GeoServer import service integration

`GeoServerImportService` (in `Honua.Postgres`) injects the optional `ISldStyleConverter` from `Honua.Core.Features.Styling.Abstractions`. When registered (the default in Honua.Server), SLD styles encountered during a GeoServer import are run through the converter and any conversion diagnostics (warnings and errors) are appended to the import warnings list. `UnsupportedStyleBehavior` (`FailImport` / `Skip` / `LogWarning`) consistently gates both conversion errors and missing SLD content (`SldContent` empty / null) — the `Skip` and `FailImport` paths short-circuit the per-style import, while `LogWarning` records the diagnostic and continues.

The bulk import path validates the SLD payload and surfaces diagnostics; it does **not** persist the converted MapLibre JSON to the catalog. Imported style resources whose SLD parsed and converted cleanly carry the note `"SLD validated; apply via per-layer admin SLD endpoint to persist MapLibre style"`. When the SLD is missing, fails to convert, or runs without a registered converter (each gated through `UnsupportedStyleBehavior`), the resource note becomes `"SLD not validated; review warnings before applying via per-layer admin SLD endpoint"` so operators can distinguish validated styles from styles that only flowed through with warnings. To store the converted style, call `POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld` for each target layer (the admin endpoint is the single canonical persistence path; see [Endpoints](#endpoints)).

If the converter is not registered (e.g. embedded scenarios that omit Honua.Server), the legacy unsupported-style behavior applies and a warning is recorded so operators see a clear migration path.

## Follow-on work

The following items are intentionally out of scope for the server slice and tracked separately:

- Admin UI upload/preview/apply workflow (honua-server-admin or a dedicated UI ticket).
- A standalone `honua-migrate sld-convert` CLI for offline batch conversion.
- High-fidelity SLD export including data-driven expressions via `<ogc:Function>` (Pro/Enterprise).
- Source SLD persistence (storing the original SLD alongside the converted MapLibre JSON) to support re-export from the original document.

## See also

- [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md)
- [ADR-0002: MapLibre as canonical style format](../contributor/adr/) (see the ADR index)
- [OGC Styled Layer Descriptor 1.0](https://www.ogc.org/standards/sld)
- [OGC Symbology Encoding 1.1](https://www.ogc.org/standards/symbol)
