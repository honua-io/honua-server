# ImageServer identify catalog contract

The `catalogItems` member of an ImageServer identify response is a GeoServices REST feature-set
object. Its `features` contain `attributes.OBJECTID`, `attributes.Name`, and, when
`returnGeometry=true`, polygon `geometry.rings`. A flat array of
`id`/`name`/`footprint` records is not this contract. Clients that consumed Honua's
previous array should read `catalogItems.features` and its feature attributes.

The response follows the GeoServices REST identify contract.

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
