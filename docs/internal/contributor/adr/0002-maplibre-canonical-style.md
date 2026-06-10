# ADR-0002: MapLibre as Canonical Style Format

## Status
Accepted. The canonical-format decision (MapLibre v8) stands. The "one style per layer"
*storage* framing below is superseded as the target by **ADR-0048**, which adopts first-class,
reusable, styleId-keyed style resources (one style → many layers) and defines the OGC API –
Styles contract on top. MapLibre remains the single canonical encoding throughout.

## Context
Honua serves layers via multiple protocols (FeatureServer, OGC API Features, OData). Each protocol has different styling expectations:
- GeoServices REST clients expect `drawingInfo` with renderer definitions
- MapLibre/Mapbox clients expect Style Spec v8 JSON
- OGC API Styles (future) expects OGC Styles

Need a single source of truth for layer styles that can be converted to protocol-specific formats.

## Decision
Store MapLibre Style Spec v8 as the canonical format. Convert to GeoServices `drawingInfo` on-the-fly for FeatureServer responses.

**One style per layer, stored as MapLibre JSON:**
```sql
ALTER TABLE honua.layers ADD COLUMN maplibre_style JSONB;
ALTER TABLE honua.layers ADD COLUMN geoservices_drawing_info JSONB; -- Cache
```

**Rationale:**
- MapLibre is open standard, well-documented, widely adopted
- GeoServices renderer format is more complex
- MapLibre → GeoServices conversion is straightforward for Simple/UniqueValue/ClassBreaks
- Admin UI stores and edits MapLibre JSON, so native format avoids conversion; visual Maputnik editing is tracked as UI backlog rather than current source behavior.

## Consequences

### Positive
- Single source of truth for styles
- No style drift between protocols
- MapLibre ecosystem tools work natively
- A future embedded Maputnik editor can work without conversion

### Negative
- Advanced GeoServices renderer features may not round-trip perfectly
- Must implement and maintain MapLibre → GeoServices converter
- Importing GeoServices REST services requires GeoServices → MapLibre conversion

### Mitigation
- Cache GeoServices `drawingInfo` in layer table to avoid repeated conversion
- Support Simple, UniqueValue, ClassBreaks renderers (covers 95% of use cases)
- Document unsupported GeoServices renderer types
