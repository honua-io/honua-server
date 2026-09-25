# Optional verified nightly GDAL worker publication

`worker-gdal-image.yml` remains the canonical worker build and verification lane.
PR and Monday scheduled runs only validate the image and publish scan evidence.
An explicit dispatch defaults `publish_nightly` to false. That dispatch also
rehearses export/load of the gated image and retains its bundle for seven days,
without publishing an image.

After review, an operator can select `publish_nightly=true` on canonical `trunk`.
Publication requires the image/boundary/actual container handoff, real PDAL,
native public API, existing blocking Trivy policy and SARIF jobs to succeed.
The separate publication job alone receives new OIDC/attestation authority.
Feature branches, PR events, schedules and noncanonical repositories cannot
publish, even if they request it. The publisher independently checks those
conditions before registry mutations.

The build job exports the exact tested `linux/amd64` image. Its bundle records
the archive checksum, Docker config digest, exact source/run/attempt, actual
non-skipped passing TRXs and scan hashes. Publication loads that archive without
rebuilding, verifies all identities, and pushes only a unique
`nightly-<full-source-sha>-<run-id>-<attempt>` tag under
`ghcr.io/honua-io/honua-worker-gdal`. It resolves the registry manifest digest,
checks its config digest equals the tested image ID, and pulls the digest to
recheck the runtime revision and architecture.

The workflow then attests that registry digest and independently verifies the
signed source/workflow identity before marking the retained publication receipt
`provenance_verified:true`. Failed publication/provenance steps retain available
evidence and do not create a passing receipt. A pushed image whose attestation
failed is not accepted evidence. No released/version/latest tag is written.

The receipt explicitly says `qualification:false`: image build, native smoke
and provenance are prerequisites, not whole-catalog or frozen-candidate
qualification. Pair the published worker with a separately verified server
image from the same source in the release lock before qualifying either. This
lane currently publishes only amd64; it does not claim an arm64 worker.

Rerun the whole workflow after a failed publication attempt: a bundle from a
different run attempt is rejected. GHCR package visibility and consumer pull
authorization remain operator configuration; a successful authenticated push
does not establish anonymous pull availability. No secrets are added or logged.
