# ADR-0007: Embedded Maputnik Style Editor

## Status
Accepted; the embedded editor is re-homed to **honua-console** (the active
admin/console UI home). The earlier routing to `honua-server-admin#80` is
**stale** — `honua-server-admin` is archived/dead and is no longer an active
target. Portal saved-map style overrides remain tracked in `honua-portal#39`
(the portal is a separate end-user surface). The honua-server side of this ADR
has no remaining work — the server delivers the canonical style via the OGC
API – Styles surface (`/ogc/styles/{styleId}`, ADR-0048) and the legacy
`/api/styles/{layerId}.json` alias; the editor integration lives in
honua-console.

**Product decision — dual-mode editor.** The honua-console style editor is
**dual-mode**: it supports both **MapLibre/Maputnik** visual authoring and
**Esri-renderer (`drawingInfo`)** authoring (simple / unique-value /
class-breaks) for users familiar with the Esri model. Both modes author the
single canonical style over `/ogc/styles`; the server round-trips MapLibre ↔
Esri `drawingInfo` (ADR-0002), so there is one source of truth regardless of
the authoring mode the user chooses.

## Context
MVP needs a visual style editor for MapLibre styles. Options:
- Build custom style editor in Blazor
- Embed Maputnik (open-source MapLibre style editor)
- No visual editor (JSON only)

## Decision
Embed Maputnik in **honua-console** via iframe with postMessage API for style
exchange, as one mode of the dual-mode (MapLibre/Maputnik + Esri-renderer)
editor described in Status. The original framing targeted the now-archived
`honua-server-admin`; that target is dead and the editor re-homes to
honua-console (which already hosts the styleId-picker foundation). Portal
saved-map style overrides are tracked separately in `honua-portal#39`.

**Integration:**
```html
<iframe src="/maputnik/index.html" id="maputnik"></iframe>
```

```javascript
// Send style to Maputnik
maputnikFrame.postMessage({ type: 'setStyle', style: layerStyle }, '*');

// Receive edited style
window.addEventListener('message', e => {
  if (e.data.type === 'styleChanged') saveStyle(e.data.style);
});
```

## Consequences

### Positive
- Full-featured style editor without building one
- Maputnik is actively maintained, well-tested
- Supports all MapLibre style features
- Significantly reduces MVP development time

### Negative
- Adds ~2MB to admin bundle (Maputnik assets)
- iframe integration can be tricky (CORS, CSP)
- Maputnik UI may not match admin UI styling
- Dependent on external project's maintenance

### Mitigation
- Bundle Maputnik assets in Docker image
- Configure CSP to allow iframe communication
- Document Maputnik version in use
