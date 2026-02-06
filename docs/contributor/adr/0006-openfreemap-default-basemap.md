# ADR-0006: OpenFreeMap as Default Basemap

## Status
Accepted

## Context
Admin UI needs a basemap for map preview and style editing. Options:
- OpenStreetMap raster tiles
- MapTiler (requires API key)
- Mapbox (requires API key, usage fees)
- OpenFreeMap (free, no API key)
- Stadia Maps
- Self-hosted tiles

## Decision
Use OpenFreeMap as the default basemap, with MapTiler as an optional alternative.

**Configuration:**
```bash
Basemap__Provider=openfreemap  # default
Basemap__Provider=maptiler     # requires Basemap__ApiKey
```

## Consequences

### Positive
- Zero configuration for basic usage
- No API keys required for default
- Free for all usage levels
- MapTiler option for users who want premium tiles

### Negative
- OpenFreeMap less polished than commercial options
- Limited style options compared to MapTiler/Mapbox
- Depends on third-party free service

### Notes
- Users can always point MapLibre at any tile source
- Self-hosted tiles possible but not documented for MVP
- Post-MVP, we may migrate the default basemap to Protomaps to support self-hosted/offline deployments
  and tighter control over tile data (would require hosting PMTiles plus style/sprites/glyphs).
