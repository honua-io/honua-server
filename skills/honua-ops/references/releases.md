# Release readback

Call `honua_platform_release_status` on the management endpoint.

For the current pin, and again after an approved upgrade, success is all of these:

- `isCoVersioned` is true and `skewedIds` is empty.
- The serving target's `observedArtifactReference` equals the `server` digest in `images.json`, or `HONUA_RELEASE_IMAGE` after an approved upgrade, and `skewed` is false.

`releaseVersion` is the declared target. It does not prove the serving container moved. `observedImageId` is a local image id and is not the pin. Compare builds by `deploymentRevision` or the image digest, not by SemVer.

A rollback is `honua_propose_rollback`: a forward deploy to a prior revision. Review the selected revision and backup in the proposal's `diff` and `dryRun` before approval. Rollback to the configured release image is refused. After the proposal reaches `Succeeded` or `Completed`, the serving target's `observedArtifactReference` must equal the immutable image in that proposal, not the configured release pin. That target is then `skewed`, and its id is in `skewedIds`. Report that skew. It does not mean the rollback failed.

For an image with no schema label, set `HONUA_PRIOR_MAX_SCHEMA` from that image's own metadata. Do not guess a maximum schema.
