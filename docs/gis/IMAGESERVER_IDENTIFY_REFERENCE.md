# ImageServer identify catalog contract

The `catalogItems` member of an ImageServer identify response is a GeoServices REST feature-set
object. Its `features` contain `attributes.OBJECTID`, `attributes.Name`, and, when
`returnGeometry=true`, polygon `geometry.rings`. A flat array of
`id`/`name`/`footprint` records is not this contract. Clients that consumed Honua's
previous array should read `catalogItems.features` and its feature attributes.

The wire shape is specified by the
[GeoServices REST identify reference](https://developers.arcgis.com/rest/services-reference/enterprise/identify-image-service/)
and was checked on 2026-09-28 against two public GeoServices REST sample services:

- [NLCDLandCover2001](https://sampleserver6.arcgisonline.com/arcgis/rest/services/NLCDLandCover2001/ImageServer),
  used by the [ImageryLayer JavaScript sample](https://developers.arcgis.com/javascript/latest/sample-code/layers-imagerylayer/).
- [CharlotteLAS](https://sampleserver6.arcgisonline.com/arcgis/rest/services/CharlotteLAS/ImageServer),
  used by the [Image Service REST reference](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/).

For each service, request its metadata, use the center of its own extent with the
declared spatial reference, and request `identify` with `f=json`,
`geometryType=esriGeometryPoint`, `returnCatalogItems=true`, and
`returnGeometry=true`. Both references return an object with `objectIdFieldName`,
`geometryType`, `spatialReference`, and `features`.

The regression tests exercise the source-generated JSON response, polygon ring
closure, feature attributes, geometry suppression, raster selection, and empty
catalogs. Spatial references describe the catalog footprints rather than the
identify point's coordinate system. Each footprint carries its own reference;
a mixed-reference catalog does not claim a single shared reference.
