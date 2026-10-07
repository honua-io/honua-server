# ArcGIS application path prefix

Honua accepts both its root routes and the conventional `/arcgis` application
prefix. For example, `/rest/services` and `/arcgis/rest/services` use the same
catalog endpoint. SOAP `/services`, compatibility `/admin/services`, and sharing
routes are available under the same prefix. A separate rewriting proxy is not
required to reach the prefixed endpoints.

The prefix becomes ASP.NET Core `PathBase` before routing. Authentication,
authorization, tenancy, and other middleware receive the canonical request path.
Generated base URLs retain the prefix. If `Public:BaseUrl` or `PUBLIC_BASE_URL` is
configured, that explicit public URL remains authoritative for service links.
`/rest/info` (`soapUrl`, `secureSoapUrl`, `tokenServicesUrl`) and the GeocodeServer
directory URL also advertise `/arcgis` when the resolved base is an origin. A base
that already ends in `/arcgis`, or that already has any other path, is left unchanged.

For ArcPy standalone routing discovery, the dictionary's `url` and `utilityUrl`
can both address `/arcgis/rest/services/geoprocessing/GPServer` on the Honua host.
The client can then discover the SOAP catalog at `/arcgis/services` and reach
the utility tasks under the same mount point.

The alias addresses URL routing. It does not establish native portal sign-in,
branch-version recognition, or desktop certification. Those behaviors require
separate native client receipts. Root and prefixed requests use the same existing
error envelopes and HTTP status contracts.

Native portal discovery also exposes `/arcgisuris.xml` at either mount point.
The XML uses the legacy `ArcGISOnlineURIList` element name, identifies the service
as Honua, and advertises its actual base URL and Sharing info ping endpoint.
It advertises `Secure` only when the resolved public base URL is HTTPS. Account,
password-recovery, update and Esri basemap links are not advertised. The route
uses the same configuration and entitlement gate as the Sharing read surface.
This bootstrap document alone does not establish native sign-in compatibility.

Sharing info also publishes `owningSystemUrl` as the actual public portal root,
including the application prefix. It agrees with `authInfo.tokenServicesUrl` so
native clients can resolve Sharing resources from the portal root. This
discovery URL does not advertise an ArcGIS release or relax token bindings.

The alias does not implement missing routing tools.
`arcpy.nax.Route` and `ServiceArea` still require the ready-to-use GP tool contracts
tracked in [#5192](https://github.com/honua-io/honua-server/issues/5192).
