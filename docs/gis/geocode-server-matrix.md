# GeocodeServer Matrix (Esri Enterprise vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](geoservices-rest-parity.md)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/geocode-service/
- https://developers.arcgis.com/rest/geocode/api-reference/overview-world-geocoding-service.htm

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua and the documented behavior is supported.
- Partial: the Esri operation/resource exists, but only a subset of documented parameters or behavior is supported.
- Not implemented: the Esri operation/resource is not exposed by Honua.

Honua exposes a single configured locator. The service is anonymous and read-only.
The default backing provider is Nominatim (OpenStreetMap); Azure Maps and Amazon
Location are also wired. Coordinates are returned in WGS84 (`wkid` 4326); a
non-default `outSR` is rejected with a clear 400 because reprojection of geocode
output is not yet implemented.

## Service resource

| Esri resource | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| GeocodeServer metadata | `/GeocodeServer` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer`, `GET/POST /rest/services/GeocodeServer` | Returns `currentVersion`, `serviceDescription`, `capabilities`, `spatialReference`, `singleLineAddressField`, `addressFields`, and `locatorProperties`. `candidateFields` and `categories` are not advertised. |

## Esri GeocodeServer operation coverage

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Find Address Candidates | `/GeocodeServer/findAddressCandidates` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates`, `GET /rest/services/GeocodeServer/findAddressCandidates` | Forward geocode from `singleLine` or structured fields. Honors `maxLocations`, `outSR` (default WKID only), `countryCode`/`countryCodes`, and `searchExtent` (passed to the provider as a search/view bounds when the provider supports it). |
| Reverse Geocode | `/GeocodeServer/reverseGeocode` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode`, `GET /rest/services/GeocodeServer/reverseGeocode` | Resolves `location` (`x,y` or JSON `{x,y}`/`{lon,lat}`) to the nearest address. Honors `outSR` (default WKID only) and `langCode` (passed to providers that support localized results). |
| Suggest | `/GeocodeServer/suggest` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/suggest`, `GET /rest/services/GeocodeServer/suggest` | Ranked suggestions from `text`. Honors `maxSuggestions`, `countryCode`/`countryCodes`, `searchExtent`, and `location` (bias) when the provider supports them. Returns `magicKey` per suggestion. |
| Geocode Addresses (batch) | `/GeocodeServer/geocodeAddresses` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses`, `GET /rest/services/GeocodeServer/geocodeAddresses` | Batch forward geocode. Accepts the Esri `addresses={"records":[...]}` payload (and a bare `records` array). Honors `outSR` (default WKID only) and `countryCode`/`countryCodes`. Enforces the provider batch-size cap. |

## Request parameter coverage

### Common

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Supports `json` and `pjson` only. Esri `html` output is not supported. |
| `provider` | Implemented (Honua extension) | Selects a specific registered geocode provider; unknown providers return 400. Omitting it uses the default provider with coordinator failover. |

### Find Address Candidates

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `singleLine` / `SingleLine` | Implemented | Primary input. |
| `address`, `city`, `region`, `postal`, `countryCode` | Implemented | Structured fields are concatenated into a single-line query when `singleLine` is absent. |
| `maxLocations` | Implemented | Positive integer; defaults to the provider's configured result count. |
| `outSR` | Partial | Only the configured default WKID (4326) is accepted; other values return 400. |
| `searchExtent` | Implemented | Accepts `xmin,ymin,xmax,ymax` or an Esri envelope JSON. Forwarded to the provider as a search/view bounds (e.g. Nominatim `viewbox`+`bounded`, Azure/Amazon `bbox`). Must be in the default spatial reference. |
| `countryCode` / `countryCodes` | Implemented | Restricts results to the given country code(s). |
| `outFields` | Not implemented | All available provider attributes are returned; field selection/projection is not applied. |
| `category` | Not implemented | No backing provider currently filters forward candidates by category. |
| `magicKey` | Not implemented | The `magicKey` returned by `suggest` is opaque and not consumed by `findAddressCandidates`. |
| `location` | Not implemented | Point biasing is not applied to forward candidates (no backing provider honors it for this operation). |
| `matchOutOfRange`, `forStorage` | Not implemented | Accepted but ignored. |

### Reverse Geocode

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `location` | Implemented | Required. Accepts `x,y` or JSON `{x,y}` / `{lon,lat}`. |
| `outSR` | Partial | Only the configured default WKID (4326) is accepted; other values return 400. |
| `langCode` | Implemented | Forwarded to providers that support localized results (Nominatim `accept-language`, Amazon Location `Language`). |
| `featureTypes` | Not implemented | Accepted but ignored; the nearest match is returned regardless of type. |
| `distance` | Not implemented | Search radius is not currently configurable from the request. |

### Suggest

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `text` | Implemented | Required. |
| `maxSuggestions` | Implemented | Positive integer; defaults to the provider's configured suggestion count. |
| `searchExtent` | Implemented | Forwarded to providers that honor suggestion bounds (Azure Maps `bbox`). |
| `location` | Implemented | Forwarded as a bias location to providers that honor it (Azure Maps, Amazon Location). |
| `countryCode` / `countryCodes` | Implemented | Restricts suggestions to the given country code(s). |
| `category` | Not implemented | No backing provider currently filters suggestions by category. |

### Geocode Addresses (batch)

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `addresses` / `records` | Implemented | Accepts the Esri `{"records":[{"attributes":{...}}]}` payload (primary) and a bare JSON array. Each record resolves `SingleLine` first, then structured fields. |
| `outSR` | Partial | Only the configured default WKID (4326) is accepted; other values return 400. |
| `countryCode` / `countryCodes` | Implemented | Applied to every record in the batch. |
| `sourceCountry`, `matchOutOfRange`, `category` | Not implemented | Accepted but ignored. |

## Known limitations

- Output is WGS84 (`wkid` 4326) only. A non-default `outSR` is rejected rather than reprojected.
- Only `f=json` and `f=pjson` responses are supported; `f=html` is rejected.
- `outFields` projection, `magicKey` round-tripping, `category` filtering, and `forStorage`/`matchOutOfRange` semantics are not implemented; unsupported parameters are accepted and ignored rather than erroring, except where validation (`outSR`, `searchExtent`, `f`) returns a 400.
- Parameter wiring is honest about provider capability: a parameter is only forwarded when at least one backing provider acts on it. The default Nominatim provider honors `searchExtent` (forward) and `langCode` (reverse); Azure Maps and Amazon Location additionally honor suggestion bounds/bias.

## Implementation evidence

- Endpoint mapping: [GeocodingEndpoints](../../src/Honua.Server/Features/Geocoding/GeocodingEndpoints.cs)
- Request parsing, validation, and adapter-to-canonical mapping: [GeocodingHandler](../../src/Honua.Server/Features/Geocoding/GeocodingHandler.cs)
- Protocol DTOs: [GeocodingModels](../../src/Honua.Server/Features/Geocoding/GeocodingModels.cs)
- Canonical request/response models: [GeocodeModels](../../src/Honua.Geocoding/Features/Geocoding/Domain/GeocodeModels.cs)
- Backing providers: [NominatimGeocodeProvider](../../src/Honua.Geocoding/Features/Geocoding/Providers/NominatimGeocodeProvider.cs), [AzureMapsGeocodeProvider](../../src/Honua.Geocoding/Features/Geocoding/Providers/AzureMapsGeocodeProvider.cs), [AmazonLocationGeocodeProvider](../../src/Honua.Geocoding/Features/Geocoding/Providers/AmazonLocationGeocodeProvider.cs)
- Integration tests: [GeocodingEndpointTests](../../tests/dotnet/Honua.Server.Tests/Features/Geocoding/GeocodingEndpointTests.cs)
