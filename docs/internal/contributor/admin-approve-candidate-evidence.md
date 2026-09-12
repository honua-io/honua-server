# Scoped approval candidate evidence

Issue: [#3365](https://github.com/honua-io/honua-server/issues/3365).

The server implementation is delivered by #3576 and #4372. The candidate
proof below exercises that implementation through the real HTTP host with
PostGIS and Redis. It also extends #4637's expired-key rotation regression
to a key carrying `admin:read` and `admin:approve`.

The [2026-09-12 receipt](admin-approve-candidate-receipt.json) records five
passing server check groups at the manifest-pinned server revision. A negative
harness check also confirmed that a floating image tag is rejected and removes
any stale success receipt before execution.

## Reproduce the server proof

Use the server digest and source revision from the release manifest. Pull the
image first, then run the certification script; it checks the image's source
label against the supplied revision before starting any containers.

```bash
python3 scripts/certification/prove-admin-approve-candidate.py \
  --image ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd \
  --source-revision 7ba422672e0c751843b17beb36e954a019cc19fb \
  --output artifacts/admin-approve-candidate.json
```

The fixture uses the documented Development Pro entitlement, a private Compose
network, random credentials, a loopback-only port, PostGIS 18/3.6, and Redis 7.2
with AOF enabled. It removes its containers and volumes on completion. The
receipt contains no credentials. This is a development entitlement fixture,
not a production license qualification or a restart/recovery proof.

The assertions are independently specified:

- Mint both keys through `AdminApiKeyEndpoints`; read back their exact grants
  and authentication eligibility. Both keys can list proposals, and both are
  denied an unrelated service access-policy mutation.
- Create two query drafts containing `{"where":"population > 42"}` through
  the Studio API, then tighten the canonical guardrail to require approval.
- Request deletion of each draft. Both requests must produce a real pending
  proposal and leave the complete draft unchanged.
- Attempt both decisions using the read-only key. Each must return 403 naming
  `admin:approve`, preserving both the proposal and the complete draft.
- Approve one deletion with the read/approve key. Require HTTP 200,
  persisted `Succeeded`, the correct separate decision actor, and HTTP 404
  when reading that draft. Reject the other deletion and require persisted
  `Rejected` with its full original draft still readable and unchanged.
- Mint an expired read/approve key. Rotation must return 404; the retained
  effective permissions must remain expired and unable to authenticate;
  an authenticated proposal-list attempt must return 401.

This proves representative admin reads and the scoped decision boundary. It
does not assert that every admin GET always returns 200 regardless of resource
existence, licensing, tenant, or other endpoint-specific requirements. Existing
auth-shard unit and integration tests remain the general permission-policy
evidence. The customer key recipe remains in
[Authentication](../../guides/secure/authentication.md).

## Console acceptance remains outstanding

At release-manifest revision
`f6c54b4396bdadb76676be7b839de71fb9a3de84` (2026-09-12), the pinned Console
source is `2cd3a73a6b103f1d66d79fcddecd455b6f19ee7f`, image digest
`sha256:f758bff932d4be9fb5831b3959bf294c2547834072d0781e0076a9012be204da`.
The candidate exists; absence of a candidate is not a reason to defer this work.

The focused receipt implementation in
[Console PR #338](https://github.com/honua-io/honua-console/pull/338), head
`c8fcbe4c5e46161ad497f4f82a51baf016d33c42`, requires
`/operate/release-witness` and its `data-release-witness` UI. Neither the page
nor the receipt producer exists in that pinned Console source. This is a
source/artifact dependency finding, not a failed browser execution receipt.
The browser producer also requires a sealed paused terminal/Studio handoff,
exact map/app/dashboard publication identities, and a separate operator bearer.
The two server draft fixtures above do not substitute for those inputs.

The remaining acceptance criterion is owned by
[Console #351](https://github.com/honua-io/honua-console/issues/351), with
[PR #360](https://github.com/honua-io/honua-console/pull/360) also carrying the
operator-identity boundary. Land the coherent Console delivery, publish and pin
its artifact, then execute its focused browser receipt against the pinned
server and the actual terminal handoff. Until that receipt passes, #3365 stays
open; server API success is not Console qualification.
