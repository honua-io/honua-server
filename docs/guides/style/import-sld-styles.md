# Import SLD styles

Convert a GeoServer SLD/SE document into a layer's stored MapLibre style server-side, instead of rewriting style files by hand.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), a published layer ([publish layers](../publish/publish-layers.md)), and an admin API key ([authentication](../secure/authentication.md)). Available in Community edition.

The conversion is best-effort and never silent: everything that cannot be translated surfaces as a structured diagnostic, and nothing is stored when error-severity diagnostics block the import.

## Steps

1. Import an SLD file (SLD 1.0 or SE 1.1, max 1 MiB) against the target layer:

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld`.

   A 200 response persists the converted style and returns `detectedVersion`, `layerCount`, the resulting `mapLibreStyle`, and a `diagnostics` array. A 422 means no convertible symbolizers — diagnostics explain why, and no partial style is stored.

2. Review the `diagnostics` array. Each entry has `severity`, `construct`, `message`, and `ruleName`; warnings mean the rule was kept but a property was dropped (see the table below).

3. Export the stored style back to SLD 1.0 for round-trip validation or use in other tools:

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/metadata/layers/{layerId}/style/export-sld`.

   The `X-Sld-Diagnostic-Count` response header reports how many export diagnostics were emitted (`X-Sld-Diagnostics` carries them as JSON when non-zero). A 422 means the stored style has no SLD-expressible layers.

## Supported symbolizers

| SLD construct | MapLibre output | Notes |
|---|---|---|
| `PointSymbolizer` / `Mark` | `circle` | Non-circle well-known names warn; no sprites are generated. |
| `PointSymbolizer` / `ExternalGraphic` | `symbol` | `icon-image` set to the href; remote URIs are recorded, never fetched. |
| `LineSymbolizer` | `line` | Stroke color, width, opacity, dasharray, linecap, linejoin. |
| `PolygonSymbolizer` | `fill` + separate `line` outline | Outline lives on a dedicated line layer to avoid double-stroking. |
| `TextSymbolizer` | `symbol` labels | Only `<ogc:PropertyName>` labels map to `{field}`. |
| `Min`/`MaxScaleDenominator` | `minzoom` / `maxzoom` | Web Mercator approximation. |
| Comparison filters, `And`/`Or`/`Not` | MapLibre filter expressions | `PropertyIsBetween` decomposes into `>= AND <=`. |

## Unsupported constructs (warning, rule preserved)

`RasterSymbolizer`, `GraphicFill`/`GraphicStroke` (pattern fills), OGC `Function` expressions, spatial/temporal predicates (`BBOX`, `Intersects`, `DWithin`, …), `PropertyIsLike`, `PropertyIsNull`, `ElseFilter`, `VendorOption` and GeoServer extensions, `LabelPlacement`, `Graphic` rotation/size, `Transformation`, `UserLayer`/`NamedStyle`. Affected rules render unfiltered or with the offending property omitted.

On export, data-driven MapLibre expressions (`match`, `step`, `interpolate`, `case`) cannot be expressed in plain SLD 1.0 — each warns and the property is omitted. Export always targets the SLD 1.0 namespace; SE 1.1 export is not implemented.

## Verify

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/metadata/layers/{layerId}/style`.

Expected (trimmed): the converted document with a bumped revision.

```json
{ "mapLibreStyle": { "version": 8, "layers": [ { "type": "fill" } ] }, "styleVersion": 1 }
```

## Troubleshoot

- **400 with a generic parse error** — malformed XML, or the document used DTDs/external entities, which are rejected by design. Raw parser messages are never echoed.
- **413** — the payload exceeds the 1 MiB cap; split the SLD or strip unused rules.
- **422 with `SLD document contained no convertible symbolizers`** — every rule fell in the unsupported set; check the diagnostics for the specific constructs.
- **Icons missing after import** — `ExternalGraphic` hrefs are never fetched by the server; supply the sprite to your map client separately.
- **Import succeeded but a rule renders everything** — its filter used an unsupported operator and was dropped (the rule renders unfiltered); see [troubleshooting](../deploy/troubleshooting.md) for log correlation.

## Next steps

- [Style map layers](style-maps.md)
- [Migrate from GeoServer](../migrate/from-geoserver.md)
