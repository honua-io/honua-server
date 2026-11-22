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

## Approval and readback

Inspect every mutation response before using draft or version fields. Approval
can appear as a `RequiresApproval` operation result or as an error with
`approvalRequired: true`, a `proposalId` and a resource URI. Retain the original
proposal identity; do not repeat the mutation to work around an approval wait
or a lost response. Read the existing proposal or operation first.

When the client supports URL elicitation, open the server-provided approval URL
for a human to review in Console. Otherwise, direct the human to the recorded
proposal in Console's Approvals inbox. The human uses an interactive password
or OIDC session and reviews the exact operation and resource. An agent or API
key cannot approve. Opening the URL or accepting a client prompt does not
record server approval.

Resume the original request when supported, or read the returned proposal URI
(`honua://proposals/{id}`). Wait for `Succeeded` or `Completed`; approval and
running states are not completion. A publication proposal can report `Active`;
verify its published version and audience. Stop on `Failed`, `Rejected` or `Cancelled`
and report the outcome. On `Indeterminate`, stop and report that the outcome is
uncertain; retain the original proposal identity and do not submit the mutation
again. Human approval does not supply missing requester permissions. If the installation rejects the requester's authority, report that
blocker to the operator; do not switch to a more privileged credential.

After completion, recover the resulting resource identities from the completed
operation or proposal's available result fields. Re-read the draft with
`honua_studio_get_draft` for its current generation before the next edit; verify
a saved version with `honua_studio_get_version` before publication. A resumed
request may return only status, without a draft or version payload. If the
installation exposes no resulting identity, report that gap and stop rather
than inventing an id, reusing a stale generation or submitting a second mutation.
