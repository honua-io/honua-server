---
type: guide
title: "Import SLD styles"
description: "Convert a GeoServer SLD/SE document into a layer's stored MapLibre style server-side, instead of rewriting style files by hand."
resource: "honua://capability/styling.ogc-api-styles"
---
# Import SLD styles

Convert a GeoServer SLD/SE document into a layer's stored MapLibre style server-side, instead of rewriting style files by hand.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), a published layer ([publish layers](../publish/publish-layers.md)), and an admin API key ([authentication](../secure/authentication.md)). Available in Community edition.

The conversion is best-effort and never silent: everything that cannot be translated surfaces as a structured diagnostic, and nothing is stored when error-severity diagnostics block the import.

## Steps

1. Import an SLD file (SLD 1.0 or SE 1.1, max 1 MiB) against the target layer:

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `POST /api/v1/admin/metadata/layers/{storageLayerId}/style/import-sld`.

   A 200 response persists the converted style and returns `detectedVersion`, `layerCount`, the resulting `mapLibreStyle`, and a `diagnostics` array. A 422 means no convertible symbolizers — diagnostics explain why, and no partial style is stored.

2. Review the `diagnostics` array. Each entry has `severity`, `construct`, `message`, and `ruleName`; warnings mean the rule was kept but a property was dropped (see the table below).

3. Export the stored style back to SLD 1.0 for round-trip validation or use in other tools:

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/metadata/layers/{storageLayerId}/style/export-sld`.

   The `X-Sld-Diagnostic-Count` response header reports how many export diagnostics were emitted (`X-Sld-Diagnostics` carries them as JSON when non-zero). A 422 means the stored style has no SLD-expressible layers (for example only background layers, or symbol layers whose `icon-image` is data-driven and that have no text).

## Supported symbolizers

| SLD construct | MapLibre output | Notes |
|---|---|---|
| `PointSymbolizer` / `Mark` | `circle` or `symbol` | `circle` maps to a `circle` layer; `square`, `triangle`, `star`, `cross`, and `x` map to `symbol` layers (`icon-image: honua-mark-<name>`) with `icon-color`, `icon-size`, and `icon-rotate` and warn that the client sprite must supply that icon. Other well-known names warn and fall back to `circle`. |
| `PointSymbolizer` / `ExternalGraphic` | `symbol` | `icon-image` set to the href and `Rotation` mapped to `icon-rotate`; remote URIs are recorded, never fetched. |
| `LineSymbolizer` | `line` | Stroke color, width, opacity, dasharray, linecap, linejoin. |
| `PolygonSymbolizer` | `fill` + separate `line` outline | Outline lives on a dedicated line layer to avoid double-stroking. |
| `TextSymbolizer` | stored `symbol` `text-field` | An `<ogc:PropertyName>` label is stored as `{field}`; `LabelPlacement` maps to `symbol-placement`, `text-anchor`, `text-offset`, and `text-rotate`. The server does not draw it, and this import does not emit FeatureServer `labelingInfo`. An author-supplied `drawingInfo` can still carry `labelingInfo`. See [Feature labeling is not served](style-maps.md#feature-labeling-is-not-served). |
| `Min`/`MaxScaleDenominator` | `minzoom` / `maxzoom` | Web Mercator approximation. |
| Comparison filters, `PropertyIsLike`, `ElseFilter`, `And`/`Or`/`Not` | MapLibre filter expressions | `PropertyIsBetween` decomposes into `>= AND <=`; `PropertyIsLike` compiles wildcard patterns into string expressions; `ElseFilter` negates sibling rules in the same `FeatureTypeStyle`. |
| `Recode`, `Categorize`, `Interpolate` | `match`, `step`, `interpolate` | Supported as both SE 1.1 elements and OGC `Function` elements on paint and layout parameters. |

## Unsupported constructs (warning, omitted)

`RasterSymbolizer`, `GraphicFill`/`GraphicStroke` (pattern fills), non-categorization OGC `Function` expressions, spatial/temporal predicates (`BBOX`, `Intersects`, `DWithin`, …), `PropertyIsNull`, mixed `%` + `_` `PropertyIsLike` patterns, `VendorOption` and GeoServer extensions, `Transformation`, `UserLayer`/`NamedStyle`. Rules whose filter uses an unsupported predicate are omitted instead of rendering unfiltered; unsupported symbolizer properties are omitted with a warning.

On export, data-driven MapLibre expressions (`match`, `step`, `interpolate`, `case`) cannot be expressed in plain SLD 1.0 — each warns and the property is omitted. Export always targets the SLD 1.0 namespace; SE 1.1 export is not implemented.

## Verify

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/metadata/layers/{storageLayerId}/style`.

Expected (trimmed): the converted document with a bumped revision.

```json
{ "mapLibreStyle": { "version": 8, "layers": [ { "type": "fill" } ] }, "styleVersion": 1 }
```

## Troubleshoot

- **400 with a generic parse error** — malformed XML, or the document used DTDs/external entities, which are rejected by design. Raw parser messages are never echoed.
- **413** — the payload exceeds the 1 MiB cap; split the SLD or strip unused rules.
- **415 with an empty body** — the import request did not send an XML `Content-Type`; send `application/xml` or `text/xml`.
- **422 with `SLD document contained no convertible symbolizers`** — every rule fell in the unsupported set; check the diagnostics for the specific constructs.
- **Icons or marks missing after import** — `ExternalGraphic` hrefs and `honua-mark-<name>` well-known marks are never fetched or synthesized by the server; supply the sprite to your map client separately.
- **Import succeeded but a rule is missing** — its filter used an unsupported operator and the rule was omitted rather than rendering unfiltered; check the `diagnostics` array and [troubleshooting](../deploy/troubleshooting.md) for log correlation.

## Next steps

- [Style map layers](style-maps.md)
- [Migrate from GeoServer](../migrate/from-geoserver.md)
