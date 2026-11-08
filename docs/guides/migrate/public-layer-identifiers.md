---
type: how-to
title: "Migrate public Layer identifiers for rc.2"
description: "Update clients to separate storage handles, Service-local indices, and graph Layer identities."
---

# Migrate public Layer identifiers for rc.2

Use matching rc.2 server and client contracts. This is a breaking change before the first supported release; the server accepts no storage-handle aliases on the affected public protocol routes.

1. Regenerate admin clients from the matching OpenAPI document. Pass `storageLayerId` to global metadata and Connection routes. Pass `serviceLayerIndex` together with its Service on Service-scoped routes. Update the corresponding response fields, including related storage handles.
2. Update MCP arguments and result readers to `serviceLayerIndex`, including each entry in `render_map.layers`. Read the index and Service together from discovery; do not substitute a storage handle.
3. Refresh WFS capabilities and use the advertised feature type. Its local name encodes the graph Layer ID with reversible XML NCName encoding. Refresh service-scoped WMS capabilities and use its advertised Service-local name.
4. Replace saved Records layer IDs with `layer:{graph Layer ID}`. Follow returned alternate links for protocol-specific collection names and Service-local indices.
5. Use the style URL advertised by TileJSON or an OGC Features collection. Public MapLibre style URLs use the graph Layer ID as `publicationId`; numeric storage handles no longer select styles.
6. Rebind a test Layer to another storage handle and verify that its WFS, WMS, Records and style identities remain unchanged. Qualify external clients and executable documentation against the matching candidate.

See the [rc.2 breaking release note](../../reference/release-notes/2026.1-rc.2.md) for old-to-new mappings and the [Layer glossary](../../concepts/domain/layer.md) for identity definitions.
