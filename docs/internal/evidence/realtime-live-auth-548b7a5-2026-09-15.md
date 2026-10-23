# Live authorization boundaries on the 2026.1 candidate 548b7a5 (server#4776, #4777, #4778)

Date: 2026-09-15. Candidate: `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`, revision `548b7a5263da5a3f2381eb43f232687cdf92b0bf` (release PR #349). The pin contains the fixes from PR #4851 (merge commit `5d9ecfdb2e`).

Two independent live runs against the pinned image close the candidate-bound criteria. PR #4851 already added the in-repo regressions.

## 1. honua-sdk-js#1692 live authorization receipt (hosted)

- Run: https://github.com/honua-io/honua-sdk-js/actions/runs/34918119363 (Realtime Cross-Transport Conformance, `workflow_dispatch`, attempt 1, success). Artifact `realtime-cross-transport-conformance-34918119363` (id 10377296533).
- SDK revision: `a5633b25b8a266ada68cd90d5909047d9a9e7cc3`. The server revision and image in the receipt are the pin above.
- Retained here: `realtime-live-auth-548b7a5-2026-09-15-sdk-receipt.json.gz` (`honua.realtime-preview-evidence.v2`). Coverage is 20 rows, 20 passed, none failed.

Rows named by the issues:

| Row | Result | Detail from the receipt |
|---|---|---|
| `feature-stream/websocket/token-expiry` | passed | terminated 30 ms after the boundary, close `1008 authorization-ended`; expired credential 401, anonymous 401 |
| `feature-stream/websocket/token-revocation` | passed | terminated 159 ms after the boundary, close `1008 authorization-ended`; revoked credential 401 |
| `feature-stream/odata/token-expiry` | passed | 401 9 ms after the boundary; anonymous 401; unscoped credential 404 |
| `feature-stream/odata/token-revocation` | passed | 401 50 ms after the boundary; anonymous 401; unscoped credential 404 |
| `token-expiry` on feature-stream SSE and on SensorThings SSE and WebSocket | passed | the producer accepts a row only when termination falls in `[expiresAt, expiresAt + 5 s]` |
| `feature-stream/odata/tenant-isolation` | passed | tenant-a credential on tenant-b layer 404, and the reverse 404 |

## 2. Boundary replay on the pinned image (local Docker, same topology)

The replay is `realtime-live-auth-548b7a5-2026-09-15-replay.sh`, which runs `realtime-live-auth-548b7a5-2026-09-15-probe.mjs`. It boots the digest through honua-sdk-js trunk `555c30eae` `scripts/realtime-live-candidate-deployment.sh`, unmodified, with this topology:

- PostGIS and Redis.
- The candidate's own `tests/seed/client-compat-v1.sql` and the two-tenant overlay: layer 10 is tenant-a, layer 11 is tenant-b, `allowAnonymous: false`, and the `reader` role is admitted.
- Staging with the image's `appsettings.Production.json`.
- Default `MultiTenancy` configuration: `DefaultTenantId` is unset, so it is `public`.

The replay records the cells the receipt does not: the WWW-Authenticate challenge, invalid bearers, the absence of a payload, three consecutive WebSockets per boundary, and the OData expiry edge measured by send and receive time. The transcript contains no credential.

Transcript: `realtime-live-auth-548b7a5-2026-09-15-transcript.json`, deployment fingerprint `sha256:3bc871386a814216d9802542a4ac0dd33764212f97994e0e685887dce6308eb8`, observed revision `548b7a5263…`. Result: **17/17 cells passed.**

| Issue | Cell | Observed |
|---|---|---|
| #4778 | tenant-a reader on layer 10 (control) | 200 |
| #4778 | anonymous on layer 10, and on layer 11 | 401, `WWW-Authenticate: ApiKey realm="Honua Admin", header="X-API-Key"`, no rows |
| #4778 | invalid bearer on layer 10 | 401, `WWW-Authenticate: Bearer`, no rows |
| #4778 | revoked bearer on layer 10 (200 before revoke) | 401, `WWW-Authenticate: Bearer`, no rows |
| #4778 | expired bearer on layer 10 | 401, `WWW-Authenticate: Bearer`, no rows |
| #4778 | tenant-b reader on layer 10, and tenant-a reader on layer 11 | 404 `ResourceNotFound`, no rows (tenant concealment kept) |
| #4778 | `?token=` query on OData, which is not an OData credential transport | 400 `InvalidQueryOption`, no rows |
| #4777 | OData polled every ~100 ms across the advertised `expires` | last admission sent 107 ms before `expires` and received 12 ms before it; first refusal sent 89 ms after `expires` |
| #4776 | feature-stream WebSocket revocation, three consecutive sockets | `1008 authorization-ended` at +986, +993 and +999 ms after the revoke request |
| #4776 | feature-stream WebSocket expiry, three concurrent sockets | `1008 authorization-ended` at +974, +990 and +1002 ms after `expires` |

An anonymous caller that presented no portal token gets the shared access-policy challenge (`AccessPolicyHelpers.AppendAuthenticationChallenge`). `Bearer` is advertised once a portal-token transport was attempted, which is the behavior #4851 specified.

The replay needs about 4 minutes. Run it with `HONUA_SDK_JS_CHECKOUT=<honua-sdk-js trunk checkout with node_modules>` and `HONUA_REPLAY_OUTPUT=<path>`. Add `HONUA_REPLAY_ANONYMOUS_PULL=1` when a host credential helper breaks anonymous GHCR pulls. Port 18190 is the default. The replay tears its containers down on exit.

## In-repo regressions (PR #4851, on trunk)

| Issue | Regression |
|---|---|
| #4776 | `FeatureStreamEndpointsTests.WebSocket_OnKestrel_CredentialExpiresOrIsRevoked_ClosesAuthorizationEndedBeforeTransportEnds`: real loopback Kestrel with `ClientWebSocket`, for expiry and revocation |
| #4777 | `PortalTokenIssuerRedisExpiryTests`: Redis-backed distributed cache, valid shortly before `ExpiresAt`, invalid at and after it |
| #4778 | `FeatureStreamEndpointsTests.ODataRead_TenantScopedProtectedLayerUnderDefaultTenant_ChallengesMissingOrEndedCredential` (`MultiTenancy:DefaultTenantId=public`; anonymous, invalid, expired, revoked) and `LayerValidationHelpersV2Tests.ValidateLayerWithAccessV2_TenantScopedLayerHiddenFrom*` |

A focused run on trunk `85765b1aed` (which contains #4851) passed 22/22 with 0 skipped. The run used `Honua.Server.Tests` with the filter above, real PostGIS and Redis fixtures, and loopback Kestrel:

- all 8 `ODataRead_…ChallengesMissingOrEndedCredential` cases: anonymous, invalid, expired and revoked, each with and without the candidate OIDC composite scheme;
- `PortalTokenIssuerRedisExpiryTests.ValidateAsync_RedisAnswers_TokenValidUntilAdvertisedExpiryAndInvalidFromIt`;
- both `WebSocket_OnKestrel_…` cases (expiry and revocation);
- all 12 `LayerValidationHelpersV2Tests`.

## Disposition

| Issue | Criterion | Status |
|---|---|---|
| #4776 | Real container of the candidate: expired or revoked credential gets `1008 authorization-ended` within 5 s, before the transport closes | met (receipt rows; replay 6/6 sockets) |
| #4776 | Regression on real Kestrel for expiry and revocation | met (#4851 test, passing on trunk) |
| #4776 | SDK receipt `feature-stream/websocket/token-expiry` and `token-revocation` pass on the re-pinned candidate | met (run 34918119363) |
| #4777 | Token validates until `ExpiresAt` and fails from it, whichever tier answers | met (replay OData edge on Redis-backed candidate; receipt expiry rows) |
| #4777 | Redis-backed regression | met (#4851 test, passing on trunk) |
| #4777 | SDK receipt `token-expiry` rows terminate in `[expires, expires + 5 s]` on the re-pinned candidate | met (run 34918119363, all five transports) |
| #4778 | Candidate with default multi-tenancy: missing, expired, revoked or invalid bearer gets 401 with a challenge and no payload; other-tenant credential gets 404 | met (replay cells) |
| #4778 | Regression with production default `DefaultTenantId` covering expiry, revocation and anonymous | met (#4851 tests, passing on trunk) |
| #4778 | SDK receipt `feature-stream/odata/token-expiry` and `token-revocation` pass on the re-pinned candidate | met (run 34918119363) |
