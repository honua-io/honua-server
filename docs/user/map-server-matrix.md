# MapServer API Matrix (Esri Enterprise vs Honua)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service-layer/

Legend:
- Implemented: endpoint exists and handles the operation.
- Partial: endpoint exists but only a subset of parameters or behavior is implemented.
- Stubbed: endpoint exists but returns "not implemented".
- Not implemented: no endpoint or handler.

## Esri REST Map Service coverage

This matrix tracks Honua coverage against the Esri REST Map Service specification:
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service/

Note: This table focuses on the operations currently implemented in Honua. Other MapServer operations are not implemented yet.

## Map Service (root resource)

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Map service metadata (resource) | `/rest/services/{serviceId}/MapServer` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer` | Service metadata + layer list. |
| Export map | `/rest/services/{serviceName}/MapServer/export` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/export` | Supports image or JSON output via `f=image` or `f=json`. |
| Identify | `/rest/services/{serviceName}/MapServer/identify` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/identify` | Supports point geometry identify. |
| Legend | `/rest/services/{serviceName}/MapServer/legend` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/legend` | Returns legend for visible layers. |

## Map Layer (query)

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Query | `/rest/services/{serviceName}/MapServer/{layerId}/query` | GET/POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/{layerId}/query` | Forwards to FeatureServer query handling. |

## Notes

- MapServer layer queries use the FeatureServer query contract. See the FeatureServer Coverage Matrix for parameter support details.
