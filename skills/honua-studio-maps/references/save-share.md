# Save styles and publication identity

Read the [saved content-version concept](https://honua.io/docs/concepts/domain/content-version/),
[style concept](https://honua.io/docs/concepts/domain/style/) and
[map publication workflow](https://honua.io/docs/guides/workflows/publish-share-map/) before saving and sharing.

A successful preview is not a durable map. A style reference must identify a
persisted style (or an inline style key in the saved composition); changing a
preview renderer alone is insufficient. In Console, save the style, including
label layout and the layer filter, then save the map version. Reopen that
version and confirm layer order, labels, filter and extent still match.

The MCP layer-style tool binds `styleRef`; it does not create an arbitrary style
document. Use Console's style editor to author and save one. If the current
host cannot operate Console, report that step as pending rather than inventing
an MCP style-write tool.

Use the latest returned generation for draft mutations. Saving refreshes the
draft generation; read it again before further editing. For publication, use
the save result's `version.itemId`, `version.versionId` and `version.contentHash`,
not a draft id or a recomputed hash.

For team or organization sharing, confirm the actual audience accepted by this
installation. Do not infer a visibility token from conversational wording.
Read back publication state and test as the intended audience when possible.
State explicitly when audience access could not be verified.
