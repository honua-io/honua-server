# Realtime candidate qualification

Realtime Preview qualification is transport-, surface-, execution-, and candidate-specific.
The required `Realtime Preview Qualification` workflow consumes the immutable SDK artifact;
local copies and route availability never qualify a row.

The SDK artifact must be produced by `.github/workflows/realtime-live-conformance.yml`, be named
`realtime-cross-transport-conformance-<run-id>`, and contain `realtime-preview-evidence.json` in
`honua.realtime-preview-evidence.v2` format. It binds the server commit and image digest, SDK
commit and package, candidate environment, workflow/run/attempt/artifact, generation window,
and an executed assertion receipt for every exact row. The release candidate job is the authority
for the candidate digest: when its digest output is present, qualification binds every receipt
and row to that exact value. Until the release-side sequencing contract is delivered, an absent
post-candidate receipt is a hard rejection; the gate still evaluates exact revision, source
workflow/run/attempt, freshness, live-lane, and non-source-built (immutable digest) admissibility.
Track the sequencing handoff in
[honua-release#269](https://github.com/honua-io/honua-release/issues/269). The workflow verifies
the source run and artifact before download, projects with `--require-qualified`, and retains the
source, diagnostics, ledger, and verdict for 180 days.

For local contract testing, use the same complete identity set:

```bash
python3 scripts/conformance/realtime/qualify_candidate.py \
  --evidence realtime-preview-evidence.json \
  --candidate-revision "$HONUA_CANDIDATE_REVISION" \
  --candidate-image "$HONUA_CANDIDATE_IMAGE_DIGEST" \
  --candidate-environment "$HONUA_CANDIDATE_ENVIRONMENT" \
  --sdk-package "$HONUA_SDK_PACKAGE" --sdk-revision "$HONUA_SDK_REVISION" \
  --workflow-repository honua-io/honua-sdk-js \
  --workflow-name 'Realtime Preview Qualification' \
  --run-id "$SDK_RUN_ID" --run-attempt "$SDK_RUN_ATTEMPT" \
  --artifact-id "$SDK_ARTIFACT_ID" \
  --source-artifact-url "$SDK_ARTIFACT_URL" \
  --qualification-run-url "$QUALIFICATION_RUN_URL" \
  --output test-results/realtime-preview-ledger.json --require-qualified
```

The 2026.1 Preview denominator covers feature-stream baseline completion, resume/gap detection,
partition recovery, token expiry, explicit token revocation, tenant isolation, and changed-tenant
resume rejection for SSE, WebSocket, and OData; OData
lossless state convergence; and separate SensorThings SSE/WebSocket loss recovery, token expiry,
and tenant isolation. Missing, failed, skipped, stale, replayed, fixture, aliased, or identity-
unbound rows reject the ledger with cell-specific reasons.

## Live authorization floor (#3871)

Feature and SensorThings streams require an authenticated credential. They revalidate the
admitted authentication scheme in a fresh dependency scope every second; the existing
request's cached authentication result is never reused. A validation timeout, revocation
observed by the configured issuer, expiry, or changed identity/role/tenant claims ends the
subscription. JWT replay admission remains a connect-time check; periodic verification of
the same admitted token still validates signature, expiry, issuer, and configured credential
policy. External issuer revocation is observable only through the configured authenticator;
the built-in portal-token issuer supports explicit revocation through its backing store.

The qualification bound is five seconds from expiry or committed revocation. The
candidate issuer configuration must use zero expiry leeway to qualify that bound.
The built-in portal-token issuer used by the regression fixture has no expiry leeway;
OIDC deployments must explicitly configure `TokenValidation.ClockSkew` to zero for
this qualification (its default is five minutes). Periodic reauthentication honors
the configured authenticator's policy, so the default OIDC skew must never be
represented as a five-second token-expiry guarantee. The issuer/configuration
fingerprint and observed timestamps in the receipt bind this distinction, and the
gate rejects termination outside the declared bound. SSE ends with
`event: status` and `{"status":"error","code":"authorization-ended"}`. WebSocket emits
close code `1008` with reason `authorization-ended`. A client reconnects with a replacement
credential and its last delivered cursor; subscription filters and tenant visibility are
evaluated again before replay. A revoked credential never becomes a replacement token.

Every `token-expiry`, `token-revocation`, `tenant-isolation`, and `tenant-scope-change` row
must retain an `authorization` object containing the SHA-256 issuer/configuration fingerprint,
two distinct `tenantIds`, distinct tenant-qualified layer/datastream `resourceIds`,
unique injected `mutationIds`, RFC3339 `issuedAt`/`expiresAt`, and
timestamped `observations` with raw frames or HTTP responses. Raw observations must fall
inside the source workflow execution. Expiry/revocation rows also retain `terminatedAt`,
`enforcementBoundMilliseconds` (at most 5000), and `terminationReason` (`authorization-ended`
for SSE/WS, `unauthorized` for OData); revocation rows retain `revokedAt`. The transcript must
cover both sides of the applicable expiry/revocation boundary, and termination must meet
the declared bound. Credentials themselves must not be retained in the receipt.

Required assertion IDs are `no-cross-tenant-payload` and `invalid-credentials-rejected`;
expiry/revocation also require `old-credential-terminated` and `replacement-resume`, and
changed-scope rows require `changed-scope-rejected`. A generic successful assertion, an
expiry row standing in for revocation, or a green projection without raw observations cannot
qualify the candidate. The source SDK workflow must produce this expanded denominator.

Local hosted tests use real portal-token issuance/revocation and isolated Postgres fixtures,
with development authentication bypass disabled. These are regression evidence. They do
not replace the exact-candidate image/SDK/issuer receipt described above; until the candidate
is cut and that receipt is available, candidate qualification remains rejected.

The 2026-09-06 native Windows .NET 10.0.100 SensorThings regression run passed
all 12 stream cases with zero failures or skips in 5m45s. Release builds used
warnings-as-errors and `-maxcpucount:4`. The local receipt is
`proofs-3871-results/sensorthings-auth-4.trx`. It covers real portal-token expiry
and revocation, bounded typed termination, exact observation values and IDs,
tenant isolation and replacement subscriptions, plus pre-handshake denials.

Ordering/duplicate depth, HA and proxy routing, Redis failover, broker outage recovery,
saturation/backpressure, and the 24–72 hour soak are 2026.2 operational-graduation rows. They
remain valuable evidence but are deliberately not required to ship realtime as Preview in
2026.1 and cannot be submitted as aliases for a Preview-floor row.

The subsequent native Windows focused FeatureServer/OData authorization run passed
all 13 tests with zero failures or skips in 4m08s (`realtime-auth-6.trx`). This run
includes actual portal expiry/revocation, OData tenant concealment and resume,
first-event cursor capture, and the cancellation/denial regression. Production and
test dependencies were rebuilt in Release with warnings as errors and
`-maxcpucount:4`.

The full native `dotnet format Honua.sln --no-restore` run completed successfully
on 2026-09-06. The canonical catalog emitter passed and the generated feature and
capability catalogs were refreshed. All 287 architecture tests then passed with
zero failures or skips in 3m16s (`architecture-realtime.trx`). These local runs
precede integration with current trunk; required head CI remains authoritative.
