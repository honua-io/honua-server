# Local PostGIS-backed geocoder

The local geocoder (`provider = "local"`) is a self-hosted, offline geocoding backend that runs
entirely against a PostGIS reference dataset you load yourself. It makes **no external service
calls**, so it is suitable for air-gapped and data-sovereignty deployments and is the substrate the
Esri `.loc`/`.lox` locator import targets.

It plugs into the shared geocoding provider abstraction, so once enabled it is served through the
same `GeocodeServer` endpoints (`findAddressCandidates`, `reverseGeocode`, `suggest`,
`geocodeAddresses`) as the hosted providers.

## Capabilities

| Operation | Supported |
| --- | --- |
| Forward geocode (`findAddressCandidates`) | Yes |
| Reverse geocode (`reverseGeocode`) | Yes |
| Suggest (`suggest`) | Yes |
| Batch (`geocodeAddresses`) | Yes (fanned out over local queries) |
| Structured input | Yes (`Address`, `Neighborhood`, `City`, `Subregion`, `Region`, `Postal`, `CountryCode`) |
| Proximity bias | No |
| Spatial reference | WGS84 (`4326`); reprojected to `outSR` by the shared transform service |

## Reference table schema

The provider reads a single reference table (default `public.honua_geocode_reference`). The schema
and table names are configurable; both are validated as strict PostgreSQL identifiers and quoted to
prevent injection.

```sql
CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE IF NOT EXISTS honua_geocode_reference (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    display_name  text NOT NULL,                 -- full formatted address returned to clients
    search_text   text NOT NULL,                 -- normalized lowercase, single-spaced match text
    address_number text,
    street_name   text,
    city          text,
    region        text,                          -- state / province
    postal_code   text,
    country       text,
    neighborhood  text,
    address_type  text,                          -- e.g. PointAddress, StreetAddress, City
    geom          geometry(Point, 4326) NOT NULL
);

-- Nearest-neighbour reverse geocoding and radius filtering.
CREATE INDEX IF NOT EXISTS ix_honua_geocode_reference_geom
    ON honua_geocode_reference USING gist (geom);

-- Forward/suggest token containment and prefix matches. pg_trgm is optional but recommended for
-- large datasets; the provider uses LIKE so it works without it.
CREATE INDEX IF NOT EXISTS ix_honua_geocode_reference_search_text
    ON honua_geocode_reference (search_text text_pattern_ops);
```

### `search_text` normalization

`search_text` must be the lowercase, trimmed, single-spaced form of the searchable address. The
provider applies the same normalization to incoming queries, so loaders should normalize on insert:

```sql
INSERT INTO honua_geocode_reference
    (display_name, search_text, address_number, street_name, city, region, postal_code, country, address_type, geom)
VALUES
    ('380 New York St, Redlands, CA 92373',
     lower(regexp_replace(trim('380 New York St Redlands CA 92373'), '\s+', ' ', 'g')),
     '380', 'New York St', 'Redlands', 'CA', '92373', 'US', 'PointAddress',
     ST_SetSRID(ST_MakePoint(-117.1956, 34.0566), 4326));
```

## Load path

1. Create the table and indexes (above).
2. Bulk-load reference records (e.g. address points, OpenAddresses extracts, or an imported Esri
   locator) with `COPY`/`INSERT`, populating `search_text` with the normalized form and `geom` as a
   WGS84 point.
3. `ANALYZE honua_geocode_reference;`

## Configuration

```jsonc
{
  "Geocoding": {
    "DefaultProvider": "local",
    "Providers": {
      "Local": {
        "Enabled": true,
        // Falls back to ConnectionStrings:DefaultConnection when omitted.
        "ConnectionString": "Host=postgis;Database=geocode;Username=app;Password=...",
        "Schema": "public",
        "Table": "honua_geocode_reference",
        "MaxCandidates": 50,
        "MaxBatchSize": 1000,
        "DefaultReverseRadiusMeters": 1000
      }
    }
  }
}
```

## Importing an Esri `.loc`/`.lox` locator

`POST /api/v1/admin/geocoding/locators/import` (admin-authorized, `multipart/form-data`) imports a
classic Esri address locator into the reference table above so it can be served through
GeocodeServer by the local provider:

| Part | Required | Content |
| --- | --- | --- |
| `locator` | yes | Classic text `.loc` definition (`key = value` properties). ArcGIS Pro binary locator payloads are rejected with an explicit error. |
| `index` | no | `.lox` binary index sidecar. It is never parsed — equivalent match indexes are rebuilt in PostGIS — and is reported as `regenerated`. |
| `referenceData` | no | CSV (header row required) with the locator's reference records. When omitted, the locator is parsed and classified only. |
| `locatorName` | no | Must match the configured `Geocoding:LocatorName` service name (case-insensitive) when supplied; defaults to it when omitted. The server registers a single GeocodeServer locator route, so any other name is rejected with `400` until per-locator registration exists. |
| `mode` | no | `replace` (default) clears the reference table first; `append` adds to it. |
| `fieldMap` | no | JSON object mapping canonical roles (`displayName`, `addressNumber`, `streetName`, `city`, `region`, `postalCode`, `country`, `neighborhood`, `addressType`, `x`, `y`) to CSV column names. Roles not listed are auto-mapped from well-known Esri reference field aliases (`HOUSE_NUM`, `STREET_NAME`, `ZIP`, `POINT_X`, ...). |

Coordinates must be WGS84 longitude/latitude. `search_text` is populated with the same
normalization the provider applies at query time. The response contains a **translation report**
with one entry per source construct (`supported`, `unsupported`, `regenerated`, or `ignored`) —
unsupported locator features (composite locators, alternate-name tables, non-WGS84 coordinate
systems, unknown properties) are reported explicitly rather than silently dropped. Match settings
(`MinimumMatchScore`, `SpellingSensitivity`, offsets, ...) are recorded verbatim in the response;
applying them to runtime candidate scoring is not yet implemented.

## Scoring

Forward-match scores are derived deterministically from the match shape against the normalized
record text (exact = 100, prefix = 90, otherwise a length-overlap-scaled 50–85), so identical input
yields identical scores across runs and platforms without relying on `pg_trgm` scoring.
