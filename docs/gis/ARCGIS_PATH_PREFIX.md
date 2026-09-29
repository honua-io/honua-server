# ArcGIS application path prefix

Honua accepts both its root routes and the conventional `/arcgis` application
prefix. For example, `/rest/services` and `/arcgis/rest/services` use the same
catalog endpoint. SOAP `/services`, compatibility `/admin/services`, and sharing
routes are available under the same prefix. A separate rewriting proxy is not
required for a direct ArcPy connection.

The prefix becomes ASP.NET Core `PathBase` before routing. Authentication,
authorization, tenancy, and other middleware receive the canonical request path.
Generated base URLs retain the prefix. If `Public:BaseUrl` or `PUBLIC_BASE_URL` is
configured, that explicit public URL remains authoritative; include `/arcgis` in
it when that is the externally advertised mount point.

For ArcPy compatibility, standalone routing dictionaries can point their `url`
and `utilityUrl` fields at the prefixed geoprocessing service address,
`https://your-honua-host/arcgis/rest/services/geoprocessing/GPServer`.

The alias addresses client connection discovery, not missing routing tools.
`arcpy.nax.Route` and `ServiceArea` still require the ready-to-use GP tool contracts
tracked in [#5192](https://github.com/honua-io/honua-server/issues/5192).
