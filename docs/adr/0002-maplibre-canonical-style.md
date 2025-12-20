# ADR-0002: MapLibre as Canonical Style Format

## Status
Accepted

## Context
Honua serves layers via multiple protocols (FeatureServer, OGC API Features, OData). Each protocol has different styling expectations:
- GeoServices REST clients expect `drawingInfo` with renderer definitions
- MapLibre/Mapbox clients expect Style Spec v8 JSON
- OGC API Styles (future) expects OGC Styles

Need a single source of truth for layer styles that can be converted to protocol-specific formats.

## Decision
Store MapLibre Style Spec v8 as the canonical format. Convert to Esri `drawingInfo` on-the-fly for FeatureServer responses.

**One style per layer, stored as MapLibre JSON:**
```sql
ALTER TABLE honua.layers ADD COLUMN maplibre_style JSONB;
ALTER TABLE honua.layers ADD COLUMN esri_drawing_info JSONB; -- Cache
```

**Rationale:**
- MapLibre is open standard, well-documented, widely adopted
- Esri format is proprietary and more complex
- MapLibre → Esri conversion is straightforward for Simple/UniqueValue/ClassBreaks
- Admin UI uses MapLibre (Maputnik editor), so native format avoids conversion

## Consequences

### Positive
- Single source of truth for styles
- No style drift between protocols
- MapLibre ecosystem tools work natively
- Embedded Maputnik editor works without conversion

### Negative
- Esri-specific advanced renderer features may not round-trip perfectly
- Must implement and maintain MapLibre → Esri converter
- Importing GeoServices REST services requires Esri → MapLibre conversion

### Mitigation
- Cache Esri `drawingInfo` in layer table to avoid repeated conversion
- Support Simple, UniqueValue, ClassBreaks renderers (covers 95% of use cases)
- Document unsupported Esri renderer types
