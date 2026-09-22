---
type: reference
title: "Dashboard lifecycle receipt harness"
description: "How to replay the honua-server#3429 dashboard lifecycle receipt against a manifest-pinned candidate image."
---
# Dashboard lifecycle receipt harness

[`compose.yml`](compose.yml) stands up the stack that
[`scripts/studio/dashboard_lifecycle_receipt.py`](../../scripts/studio/dashboard_lifecycle_receipt.py)
judges the remaining [#3429](https://github.com/honua-io/honua-server/issues/3429)
acceptance criteria on: the manifest-pinned candidate image under the Production startup
policy, with PostGIS, append-only Redis and a symmetric-key OIDC resource server.

Nothing is built. The server image is whatever digest `HONUA_IMAGE` names, so the receipt
is bound to the artifact the release train certifies rather than to a local build.

## Replay against a re-pinned candidate

Take `components.honua-server.digest` from `platform-manifest.yaml` in `honua-io/honua-release`:

```bash
export HONUA_IMAGE=ghcr.io/honua-io/honua-server@sha256:<digest>
docker compose -f docker/studio-dashboard-receipt/compose.yml -p studio-receipt up -d
```

Wait for the server to report healthy, then run the driver:

```bash
python scripts/studio/dashboard_lifecycle_receipt.py \
  --base-url http://localhost:18080 \
  --admin-key 'StudioReceiptAdmin123!' \
  --jwt-key 'studio-dashboard-receipt-signing-key-2026-1' \
  --jwt-issuer 'https://studio-receipt.honua.test' \
  --jwt-audience 'honua-studio-receipt' \
  --psql 'docker exec studio-receipt-postgres psql -U honua -d honua' \
  --redis-cli 'docker exec studio-receipt-redis redis-cli' \
  --restart-command 'docker restart studio-receipt-honua' \
  --container studio-receipt-honua \
  --candidate-image "$HONUA_IMAGE" \
  --candidate-sha '<the pinned sha>' \
  --out docs/studio/receipts/dashboard-lifecycle-<sha7>-pinned-candidate.json
```

The driver exits non-zero unless every row passes. Tear down with
`docker compose -f docker/studio-dashboard-receipt/compose.yml -p studio-receipt down -v`;
the `-v` also drops the generated key ring.

## Why these settings

Most of the environment is ordinary deployment configuration. Four entries are load-bearing
for this receipt and are easy to omit:

| Setting | Why the receipt needs it |
|---|---|
| `Licensing__Mode=Disabled` | the 2026.1 release setting. It grants every entitlement, which is what composes the Redis-backed durable proposal gateway the governed-proposal row asserts on. An unlicensed Community host fails that row closed. |
| `Studio__EndUserAuthorization__Enabled=true` | Studio package lifecycle is admin-only by default, so the driver's first call, `create_draft`, is denied with `studio_authorization/end_user_mode_disabled` and no journey runs. |
| `Operations__SecretChannel__KeyRingCertificatePath` | Production composes the durable operation secret channel and refuses to start without an operator-supplied certificate encrypting the data-protection key ring. The `keyring` service mints a throwaway one. |
| `Security__ConnectionEncryption__MasterKey` / `__Salt` | required by Production startup. |

`Guardrails__Overrides__StudioDraftMutation` is deliberately absent. Runs before the
[#4758](https://github.com/honua-io/honua-server/issues/4758) fix needed it to keep
composition direct under `Licensing__Mode=Disabled`; since `c8b1e2166` they do not, and the
built-in `studio.publication_proposal` floor still routes agent proposals to approval. If a
future candidate needs that override again, the guardrail regression is the finding — do not
re-add it to make the receipt pass.

The driver's SQL and Redis access is read-only: it joins what the client sent with what the
server durably recorded, using the client's own W3C trace id as the join key.
