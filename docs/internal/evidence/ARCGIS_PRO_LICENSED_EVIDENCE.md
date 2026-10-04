# GeoServices REST certification evidence

Desktop-client evidence production is maintained privately in
[`honua-esri-compat`](https://github.com/honua-io/honua-esri-compat). This public
repository records only protocol-level certification results and their stable
evidence identifiers.

## Covered protocol surfaces

- GeoServices REST FeatureServer
- GeoServices REST MapServer
- GeoServices REST ImageServer
- GeoServices REST GPServer
- GeoServices REST NAServer
- SOAP catalog
- WMS, WFS, and WCS
- OGC API Features, Tiles, and Maps
- STAC
- Vector tiles

## Public evidence contract

A published result must identify its certification bundle digest and applicable
cell IDs. Portal and Sharing coverage uses the append-only `CERT-PRTL-*` cells.
FeatureServer and MapServer rendering coverage uses the applicable
`CERT-RNDR-*` cells. A protocol result without those identifiers is not
certification evidence.

The REST-only `arcgis-stub` lane remains distinct from licensed certification;
it proves request and response compatibility but does not establish a desktop
certification result. Public PR gates validate only protocol fixtures and the
evidence-envelope contract. They do not execute a desktop product.

## Current status

No successful licensed certification bundle is linked here. Issue
[honua-server#1019](https://github.com/honua-io/honua-server/issues/1019)
therefore remains open. When a result is accepted, add only its bundle digest,
cell IDs, protocol surface, and pass/fail disposition to this page.
