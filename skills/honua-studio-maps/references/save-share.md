# Save styles and publication identity

A successful preview is not a durable map. A style reference must identify a
persisted style (or an inline style key in the saved composition); changing a
preview renderer alone is insufficient. In Console, save the style, including
label layout and the layer filter, then save the map version. Reopen that
version and confirm layer order, labels, filter and extent still match.

The MCP `honua_studio_set_layer_style` tool accepts either `styleRef` for an
existing style or `styleBody` for inline paint/layout overrides keyed by the
composition layer id (or `<layerId>__outline`). Do not send both. For a complete
MapLibre style, discover `honua_studio_set_map_style` and its `mapSpec` schema;
replacing the map style clears existing style references and bindings. Read
back the draft before saving and verify the intended styles remain bound.

Use the latest returned generation for draft mutations. Saving refreshes the
draft generation; read it again before further editing. Use the immutable
version's returned `itemId`, `versionId` and `contentHash` for publication,
not a draft id or a recomputed hash.

For team or organization sharing, confirm the actual audience accepted by this
installation. Do not infer a visibility token from conversational wording.
Read back publication state and test as the intended audience when possible.
State explicitly when audience access could not be verified.
