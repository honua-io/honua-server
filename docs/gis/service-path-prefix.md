# Service route roots

Honua serves GeoServices REST services from the root-mounted `/rest/services`
route. For example, a FeatureServer is available at
`/rest/services/{name}/FeatureServer`; MapServer, ImageServer, and other
services use the same root.

Configure clients with the service URL rooted at `/rest/services`. If a saved
connection includes a removed application mount before that route, update the
connection to use the root-mounted service URL.

Legacy service-discovery and administrative-resource aliases are no longer
mounted. Use root-mounted `/rest/services/{name}/...` URLs for GeoServices REST
metadata and `/api/v1/admin/services` for Honua administration operations.

Generated public links use `Public:BaseUrl` or `PUBLIC_BASE_URL` when configured.
That explicit public URL remains authoritative for service links. Portal
discovery publishes `owningSystemUrl` and `authInfo.tokenServicesUrl` from the
public portal root in `/rest/info` and `/sharing/rest/info`.
