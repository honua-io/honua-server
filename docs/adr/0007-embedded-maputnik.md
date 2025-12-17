# ADR-0007: Embedded Maputnik Style Editor

## Status
Accepted

## Context
MVP needs a visual style editor for MapLibre styles. Options:
- Build custom style editor in Blazor
- Embed Maputnik (open-source MapLibre style editor)
- No visual editor (JSON only)

## Decision
Embed Maputnik in the admin UI via iframe with postMessage API for style exchange.

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
