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

## Scoring

Forward-match scores are derived deterministically from the match shape against the normalized
record text (exact = 100, prefix = 90, otherwise a length-overlap-scaled 50–85), so identical input
yields identical scores across runs and platforms without relying on `pg_trgm` scoring.
