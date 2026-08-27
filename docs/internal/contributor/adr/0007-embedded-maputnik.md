# ADR-0007: Embedded Maputnik Style Editor

## Status

Accepted. The editor implementation is tracked by
[honua-studio#22](https://github.com/honua-io/honua-studio/issues/22), and the
Studio component is hosted by
[honua-console#324](https://github.com/honua-io/honua-console/issues/324).
Those are complementary responsibilities, not a re-home into Console. Portal
saved-map style overrides remain separately tracked in
[honua-portal#39](https://github.com/honua-io/honua-portal/issues/39).

The server delivers the canonical style through OGC API - Styles
(`/ogc/styles/{styleId}`, ADR-0048) and the legacy
`/api/styles/{layerId}.json` alias. The embedded Studio editor is dual-mode: it
supports MapLibre/Maputnik visual authoring and Esri-renderer (`drawingInfo`)
authoring (simple, unique-value, and class-breaks). Both modes author the same
canonical style; the server round-trips MapLibre and Esri `drawingInfo`
(ADR-0002).

## Context

MVP needs a visual style editor for MapLibre styles. Options:

- build a custom style editor;
- embed Maputnik, the open-source MapLibre style editor;
- offer JSON editing only.

## Decision

Embed Maputnik in the honua-studio component via an iframe and postMessage API
for style exchange. Honua Console embeds that Studio component; it does not own
a parallel editor implementation. Portal saved-map style overrides remain a
separate end-user concern.

```html
<iframe src="/maputnik/index.html" id="maputnik"></iframe>
```

```javascript
maputnikFrame.postMessage({ type: "setStyle", style: layerStyle }, "*");

window.addEventListener("message", event => {
  if (event.data.type === "styleChanged") saveStyle(event.data.style);
});
```

## Consequences

### Positive

- Full-featured style editing without duplicating Maputnik.
- One canonical style across both authoring modes.
- Studio can be embedded in Console without assigning implementation ownership to Console.

### Negative

- Adds Maputnik assets to the Studio distribution.
- iframe integration requires deliberate CSP and message-origin handling.
- Maputnik UI may not match the host UI exactly.

### Mitigation

- Bundle and pin Maputnik assets with Studio.
- Restrict postMessage origins and configure CSP for the embedded editor.
- Document the Maputnik version in use.
