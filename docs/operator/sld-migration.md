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

When error-severity diagnostics block import, the API returns 422 with a `{ "success": false, "message": "SLD import failed; see diagnostics." }` envelope. No partial stylesheet is stored, and the diagnostic count is recorded in the server log via the `SldImportRejected` structured log entry. Surfacing the diagnostic array directly in the 422 body is tracked as a follow-up; for now, callers that need the per-rule reason should retry with verbose logging or capture the conversion log.

Malformed or unsafe XML returns 400 with a generic problem detail; raw exception messages are never echoed. Payloads larger than the 1 MiB cap return 413 before parsing.

### Export response

A 200 response is `application/xml` containing a complete SLD 1.0 document. The `X-Sld-Diagnostic-Count` header reports the number of diagnostics emitted while exporting; the `X-Sld-Diagnostics` header carries the JSON-encoded diagnostic array when the count is non-zero. If the stored MapLibre style cannot be exported (no convertible layers, deserialization failure), the endpoint returns 422 with the `{ "success": false, "message": "..." }` envelope.

## Supported subset

| SLD construct | MapLibre layer type | Notes |
|---|---|---|
| `PointSymbolizer` / `Mark` (any well-known name) | `circle` | Non-`circle` well-known names emit a `Mark.WellKnownName` warning; sprites are not generated. |
| `PointSymbolizer` / `ExternalGraphic` | `symbol` | `icon-image` is set to the resource href. Remote URIs are recorded but never fetched. |
| `LineSymbolizer` (`stroke`, `stroke-width`, `stroke-opacity`, `stroke-dasharray`, `stroke-linecap`, `stroke-linejoin`) | `line` | `PerpendicularOffset` ignored with warning. |
| `PolygonSymbolizer` (`Fill` + optional `Stroke`) | `fill` plus `line` outline when `stroke-width` is set | `Displacement` ignored with warning. |
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
- `Transformation` on `FeatureTypeStyle`
- `UserLayer`, `NamedStyle` (server-side style references)

## Color handling

- CSS named colors and `#RRGGBB` are passed through.
- `#AARRGGBB` (alpha-prefixed) is normalized to `rgba(R,G,B,A)`.
- `<Opacity>` and `fill-opacity` / `stroke-opacity` CSS parameters are folded into either a separate `*-opacity` paint property or an `rgba()` value when MapLibre cannot express the property/opacity pair separately.

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

`GeoServerImportService` (in `Honua.Postgres`) injects the optional `ISldStyleConverter` from `Honua.Core.Features.Styling.Abstractions`. When registered (the default in Honua.Server), SLD styles encountered during a GeoServer import are converted in place and any conversion diagnostics are appended to the import warnings. The `UnsupportedStyleBehavior` import option still gates whether conversion errors fail the import, skip the style, or warn.

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
