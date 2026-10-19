# WMTS canonical-client assertion scope

The WMTS lane requests the maintained browser point fixture, with the authored
radius4 `#2D69A5` circle style. Positive render, zoom, second-grid and REST/KVP
checks require painted pixels at positions calculated independently from the
three authored coordinates and standard WebMercatorQuad / WorldCRS84Quad grids.
The distant fully transparent tile remains a separate positive empty-data case.

The protected mirror is `cert_auth_raster` layer2010. Both KVP and RESTful tile
requests run through OWSLib `WebMapTileService.gettile`, with API-key and OIDC
allowed reads requiring the same authored content. Anonymous, invalid API-key
and genuinely expired bearer requests must return HTTP401, the appropriate
challenge and an OWS `AccessDenied` report, with no image bytes. These are WMTS
AUTH observations; the admin probe is not their substitute. The shared auth
breadth suite for other protocols is unchanged.

Controlled blank, shifted, wrong-style/grid, wrong-denial and success-instead-of-
denial tests use isolated recorders and cannot write live certification receipts.
These controls prove assertions. Live server receipts must separately retain the
exact server image/source, harness/client versions and fixture/auth seed hashes.
A local historical-image rehearsal is code evidence, not qualification of the
selected platform candidate or AWS deployment. Existing required cases and their
nonpass semantics remain unchanged; no baseline refresh is included.
