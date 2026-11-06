# Release readback

Call `honua_platform_release_status` on the management endpoint. Success is all of these:

- `isCoVersioned` is true and `skewedIds` is empty.
- The serving target's `observedArtifactReference` equals the `server` digest in `images.json`, or `HONUA_RELEASE_IMAGE` after an approved upgrade, and `skewed` is false.

`releaseVersion` is the declared target. It does not prove the serving container moved. `observedImageId` is a local image id and is not the pin. Compare builds by `deploymentRevision` or the image digest, not by SemVer.

For an image with no schema label, set `HONUA_PRIOR_MAX_SCHEMA` from that image's own metadata. Do not guess a maximum schema.
