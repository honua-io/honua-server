# Licensed ArcGIS Pro Desktop Evidence

Desktop-client certification detail for ArcGIS Pro and ArcPy stays in private
[`honua-esri-compat`](https://github.com/honua-io/honua-esri-compat) (ruling R30,
honua-release#376). This page only names the public evidence identifier.

| Field | Value |
| --- | --- |
| Lane | `desktop-gis` |
| Distinct from | `geoservices-rest-stub` (REST-only; not a desktop client) |
| Tracking issue | [honua-server#1019](https://github.com/honua-io/honua-server/issues/1019) |
| Owning evidence issue | [honua-esri-compat#75](https://github.com/honua-io/honua-esri-compat/issues/75) |
| Envelope names | `*-desktop-gis-featureserver.cert.json`, `*-desktop-gis-mapserver.cert.json` |
| Current public result | No successful desktop run is linked. #1019 stays open until that evidence identifier exists. |

Ordinary PR gates do not require ArcGIS Pro. Runner setup, licence setup, and
client recipes are not copied into this repository.

Portal and Field Maps compatibility is nominative: Esri clients can bind through
the portal facade. The real-client receipt is the same `desktop-gis` evidence
identifier, tracked on #1372 and #1096. It is not linked here.
