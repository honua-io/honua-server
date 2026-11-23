# Release readback

Call `honua_platform_release_status` on the management endpoint.

For the current pin, and again after an approved upgrade, success is all of these:

- `isCoVersioned` is true and `skewedIds` is empty.
- The serving target's `observedArtifactReference` equals the `server` digest in `images.json`, or `HONUA_RELEASE_IMAGE` after an approved upgrade, and `skewed` is false.

`releaseVersion` is the declared target. It does not prove the serving container moved. `observedImageId` is a local image id and is not the pin. Compare builds by `deploymentRevision` or the image digest, not by SemVer.

An upgrade proposal that ends `failed` with `contract-approval-required` applied nothing; the prior revision keeps serving. Show the human the refused contract scripts and nonce. Only if they ask to proceed, propose the convergence again with `contractApproval` set to exactly those scripts and that nonce. Its approval applies only those scripts, once. A later refusal with a different plan needs a new proposal.

A rollback is `honua_propose_rollback`: a forward deploy to a prior revision. Review the selected revision and backup in the proposal's `diff` and `dryRun` before approval. Rollback to the configured release image is refused. After the proposal reaches `Succeeded` or `Completed`, the serving target's `observedArtifactReference` must equal the immutable image in that proposal, not the configured release pin. That target is then `skewed`, and its id is in `skewedIds`. Report that skew. It does not mean the rollback failed.

For an image with no schema label, set `HONUA_PRIOR_MAX_SCHEMA` from that image's own metadata. Do not guess a maximum schema.
