# GeocodeServer Matrix (Esri Enterprise vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](../../reference/compatibility/geoservices-parity.md)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/geocode-service/
- https://developers.arcgis.com/rest/geocode/api-reference/overview-world-geocoding-service.htm

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua and the documented behavior is supported.
- Partial: the Esri operation/resource exists, but only a subset of documented parameters or behavior is supported.
- Not implemented: the Esri operation/resource is not exposed by Honua.

Honua exposes a single configured locator. The service is anonymous and read-only.
The default backing provider is Nominatim (OpenStreetMap); Azure Maps and Amazon
Location are also wired. Provider coordinates are produced in WGS84 (`wkid` 4326)
and reprojected to the requested `outSR` through the shared coordinate-transform
service; a transform the service cannot perform returns a clear 400.

`magicKey` and `category` are now supported on the shared geocode interface for
every backing provider (#1867). They are implemented in the protocol adapter over
the data providers already return rather than by forwarding a provider-native
parameter (no backing provider exposes a magic-key resolution method or a forward
category parameter):

- **`magicKey`** is a self-issued, signed, opaque token. `suggest` mints one per
  suggestion that encodes the suggestion's resolvable identity (display text +
  category + originating provider); `findAddressCandidates` decodes it, re-runs a
  forward geocode for that text against the same provider, and applies the encoded
  category. The same suggestion always encodes to the same token and a token always
  resolves to the same query, so the round-trip is deterministic and provider-agnostic.
  The provider-internal ids (`nominatim_*`, Azure result id, Amazon `PlaceId`) are
  never surfaced because they are not resolvable through the providers' public
  forward-geocode endpoints. A token that fails signature verification (not issued by
  this service, or tampered) returns 400.
- **`category`** filters candidates/suggestions by the provider-supplied address type
  (`GeocodeCandidate.AddressType` / `GeocodeSuggestion.Category`) on the shared
  interface. Every provider that classifies results (Nominatim, Azure Maps, Amazon
  Location via their `GetAddressType` mapping) is filtered honestly; a result with no
  category data cannot satisfy an explicit category filter.

## Service resource

The Honua endpoint columns in this section and the operation table below are projections of `docs/gis/data/feature-catalog.json`; `DocumentationMatrixDriftTests` gates the join in both directions. Behavioral status and provider notes remain hand-authored.

| Esri resource | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| GeocodeServer metadata | `/GeocodeServer` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer`, `GET/POST /rest/services/GeocodeServer` | Returns `currentVersion`, `serviceDescription`, `capabilities`, `spatialReference`, `singleLineAddressField`, `addressFields`, `candidateFields` (the attributes every candidate emits), the `categories` array (the address/place families candidates are classified into, e.g. `Address`, `POI`, `City`), and `locatorProperties`. |

## Esri GeocodeServer operation coverage

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Find Address Candidates | `/GeocodeServer/findAddressCandidates` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/findAddressCandidates`, `GET /rest/services/GeocodeServer/findAddressCandidates` | Forward geocode from `singleLine` or structured fields. Honors `maxLocations`, `outSR` (reprojected via the shared transform service), `countryCode`/`countryCodes`, `searchExtent` (passed to the provider as a search/view bounds when the provider supports it), `magicKey` (resolves a suggestion issued by `suggest`), and `category` (narrows candidates by provider-supplied address type). |
| Reverse Geocode | `/GeocodeServer/reverseGeocode` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/reverseGeocode`, `GET /rest/services/GeocodeServer/reverseGeocode` | Resolves `location` (`x,y` or JSON `{x,y}`/`{lon,lat}`) to the nearest address. Honors `outSR` (reprojected via the shared transform service), `langCode` (passed to providers that support localized results), and the provider-dependent `distance` (search radius, meters) and `featureTypes` (type filter) parameters where a backing provider supports them (Azure Maps `radius`/`entityType`). |
| Suggest | `/GeocodeServer/suggest` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/suggest`, `GET /rest/services/GeocodeServer/suggest` | Ranked suggestions from `text`. Honors `maxSuggestions`, `countryCode`/`countryCodes`, `searchExtent`, `location` (bias), and `category` (narrows suggestions by provider-supplied category) when the provider supports them. Returns a self-issued, signed, opaque `magicKey` per suggestion that round-trips through `findAddressCandidates`. |
| Geocode Addresses (batch) | `/GeocodeServer/geocodeAddresses` | GET, POST | Partial | `GET/POST /rest/services/{locatorName}/GeocodeServer/geocodeAddresses`, `GET /rest/services/GeocodeServer/geocodeAddresses` | Batch forward geocode. Accepts the Esri `addresses={"records":[...]}` payload (and a bare `records` array). Honors `outSR` (reprojected via the shared transform service) and `countryCode`/`countryCodes`. Enforces the provider batch-size cap. |

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
| `outSR` | Implemented | Output coordinates are reprojected to the requested WKID via the shared coordinate-transform service; a transform the service cannot perform returns 400. |
| `searchExtent` | Implemented | Accepts `xmin,ymin,xmax,ymax` or an Esri envelope JSON. Forwarded to the provider as a search/view bounds (e.g. Nominatim `viewbox`+`bounded`, Azure/Amazon `bbox`). Must be in the default spatial reference. |
| `countryCode` / `countryCodes` | Implemented | Restricts results to the given country code(s). |
| `outFields` | Implemented | Restricts the returned `attributes` to the requested fields (case-insensitive). `*` or an omitted value returns all attributes; an empty field token returns 400. |
| `category` | Implemented (#1867) | Comma/semicolon-delimited category tokens (e.g. `Address`, `POI`, `City`). Candidates are filtered by their provider-supplied `AddressType` on the shared interface after the provider returns. No backing provider exposes a forward category parameter (Nominatim `/search`, Azure `/search/address`, Amazon `SearchPlaceIndexForText`), so filtering is applied to the data providers return, not forwarded upstream. `Address` matches the point/street/subaddress family. A result with no category data cannot satisfy an explicit filter. |
| `magicKey` | Implemented (#1867) | A self-issued, signed, opaque token minted by `suggest` (see the magicKey/category note in the service overview above). `findAddressCandidates` decodes it and resolves the encoded suggestion deterministically against the encoded provider, applying the encoded category. Provider-internal ids are not resolvable through the providers' forward endpoints, so they are never surfaced. A token that fails signature verification returns 400. Per the Esri contract `SingleLine`/`text` is still echoed alongside `magicKey`. |
| `location` | Not implemented | Point biasing is not applied to forward candidates (no backing provider honors it for this operation). |
| `matchOutOfRange`, `forStorage` | Not implemented (re-deferred) | Accepted but ignored. `matchOutOfRange` (out-of-range house-number interpolation) and `forStorage` (a licensing/storage billing flag) are not modeled by any backing provider. |

### Reverse Geocode

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `location` | Implemented | Required. Accepts `x,y` or JSON `{x,y}` / `{lon,lat}`. |
| `outSR` | Implemented | Output coordinates are reprojected to the requested WKID via the shared coordinate-transform service; a transform the service cannot perform returns 400. |
| `langCode` | Implemented | Forwarded to providers that support localized results (Nominatim `accept-language`, Amazon Location `Language`). |
| `featureTypes` | Implemented (provider-dependent) | Comma-delimited Esri feature-type tokens are parsed and forwarded as the canonical request's `FeatureTypes`. Honored by providers that filter reverse matches by type (Azure Maps `entityType`, with Esri tokens mapped to Azure entity types). Nominatim and Amazon Location ignore it and return the nearest match. |
| `distance` | Implemented (provider-dependent) | A positive search radius in meters; non-positive values return 400. Forwarded as the canonical request's `DistanceMeters` and honored by providers that bound the reverse search by radius (Azure Maps `radius`). Nominatim and Amazon Location ignore it. |

### Suggest

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `text` | Implemented | Required. |
| `maxSuggestions` | Implemented | Positive integer; defaults to the provider's configured suggestion count. |
| `searchExtent` | Implemented | Forwarded to providers that honor suggestion bounds (Azure Maps `bbox`). |
| `location` | Implemented | Forwarded as a bias location to providers that honor it (Azure Maps, Amazon Location). |
| `countryCode` / `countryCodes` | Implemented | Restricts suggestions to the given country code(s). |
| `category` | Implemented (#1867) | Narrows suggestions by the provider-supplied `Category` on the shared interface (no backing provider exposes a suggest category parameter). Same matching rules as the forward `category` filter. |

### Geocode Addresses (batch)

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `addresses` / `records` | Implemented | Accepts the Esri `{"records":[{"attributes":{...}}]}` payload (primary) and a bare JSON array. Each record resolves `SingleLine` first, then structured fields. |
| `outSR` | Implemented | Output coordinates are reprojected to the requested WKID via the shared coordinate-transform service; a transform the service cannot perform returns 400. |
| `countryCode` / `countryCodes` | Implemented | Applied to every record in the batch. |
| `sourceCountry`, `matchOutOfRange`, `category` | Not implemented | Accepted but ignored. |

## Known limitations

- Provider output is WGS84 (`wkid` 4326); a non-default `outSR` is reprojected through the shared coordinate-transform service, and only a transform the service cannot perform is rejected with a 400.
- Only `f=json` and `f=pjson` responses are supported; `f=html` is rejected.
- `magicKey` round-tripping and `category` filtering are implemented on the shared geocode interface for every backing provider (#1867): `magicKey` via a self-issued signed opaque token that `suggest` mints and `findAddressCandidates` resolves deterministically, and `category` via filtering on the provider-supplied address type/category. `forStorage`/`matchOutOfRange` semantics remain re-deferred (no backing provider models them); they are accepted and ignored. Unsupported parameters are accepted and ignored rather than erroring, except where validation (`outSR`, `outFields`, `searchExtent`, `distance`, `f`, and an invalid `magicKey`) returns a 400.
- Parameter wiring is honest about provider capability: a parameter is only forwarded when at least one backing provider acts on it. The default Nominatim provider honors `searchExtent` (forward) and `langCode` (reverse); Azure Maps and Amazon Location additionally honor suggestion bounds/bias; Azure Maps additionally honors reverse `distance` (`radius`) and `featureTypes` (`entityType`).

## Implementation evidence

- Endpoint mapping: [GeocodingEndpoints](../../../src/Honua.Server/Features/Geocoding/GeocodingEndpoints.cs)
- Request parsing, validation, and adapter-to-canonical mapping: [GeocodingHandler](../../../src/Honua.Server/Features/Geocoding/GeocodingHandler.cs)
- Protocol DTOs: [GeocodingModels](../../../src/Honua.Server/Features/Geocoding/GeocodingModels.cs)
- Canonical request/response models: [GeocodeModels](../../../src/Honua.Geocoding/Features/Geocoding/Domain/GeocodeModels.cs)
- magicKey codec (self-issued signed opaque token): [GeocodeMagicKey](../../../src/Honua.Geocoding/Features/Geocoding/Domain/GeocodeMagicKey.cs)
- category filter (provider-agnostic, on the shared interface): [GeocodeCategoryFilter](../../../src/Honua.Geocoding/Features/Geocoding/Domain/GeocodeCategoryFilter.cs)
- Backing providers: [NominatimGeocodeProvider](../../../src/Honua.Geocoding/Features/Geocoding/Providers/NominatimGeocodeProvider.cs), [AzureMapsGeocodeProvider](../../../src/Honua.Geocoding/Features/Geocoding/Providers/AzureMapsGeocodeProvider.cs), [AmazonLocationGeocodeProvider](../../../src/Honua.Geocoding/Features/Geocoding/Providers/AmazonLocationGeocodeProvider.cs)
- Integration tests: [GeocodingEndpointTests](../../../tests/dotnet/Honua.Server.Tests/Features/Geocoding/GeocodingEndpointTests.cs)
