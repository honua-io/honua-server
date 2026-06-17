# demo.honua.io full-capability runbook (server#1688)

Operator runbook to bring `demo.honua.io` to full demonstrable capability across the
four product pillars. It covers two separable tracks:

1. **Seed STAC collections** so the Imagery & Terrain Studio shows a live catalog.
2. **Apply a Pro license** so geocoding, realtime streaming, and FeatureServer editing
   light up.

> Scope note: the underlying server features already exist. This is **operational
> enablement** on the deployed demo. The only server code change shipped alongside this
> runbook is a fix for the `release-packages` admin API (see *Prerequisites*).

Account context (from #1688 recon): AWS `585192672263`, `us-west-2`, compute is Lambda
`honua-demo-demo-honua`; the demo DB is reachable only in-VPC via the bootstrap Lambda
`honua-demo-demo-postgis-bootstrap` (accepts `{"statements":[...]}` / `{"query":"..."}`).

Everything below is **operator-supplied where it touches secrets or the live env** —
those steps are marked **[OPERATOR]**. No secrets are committed in this repo.

---

## Prerequisites (deploy a current image first)

Both tracks require a demo image built from `trunk` at or after the commits below, then
deployed to `honua-demo-demo-honua`:

- **`release-packages` 42P08 fix** — `GET /api/v1/admin/metadata/release-packages` must
  return 200 (it 500'd with `42P08: could not determine data type of parameter $1` on the
  prior image). Fixed in `PostgresMetadataReleasePackageStore.ListAsync` (typed text
  parameters). Required only if you drive metadata admin discovery; the STAC seed below
  does not depend on it, but the demo's admin/console metadata views do.
- **License-from-content** (#1698) — `Licensing:LicenseContent` must be honored. Confirm
  with `grep` in the image or by setting it and checking the capabilities manifest.

Health gate (Redis is required for a healthy server; STAC needs a current v2 snapshot —
the seed below activates one):

```bash
curl -fsS https://demo.honua.io/healthz/live    # 200
curl -fsS https://demo.honua.io/healthz/ready   # 200 (Redis up)
```

---

## Track 1 — Seed STAC collections (REQ-001)

### Why a metadata-v2 seed, not a `honua.services`/`honua.layers` seed

The live catalog (`/stac/collections`, `/odata/Layers`, `/rest/services`) is materialized
from the **Metadata v2 graph snapshot** (`metadata.honua.io/v2alpha1`) stored in
`honua.metadata_v2_snapshots` + `honua.metadata_v2_current` — **not** from the legacy
`honua.services`/`honua.layers` tables. Raw INSERTs into the legacy tables are inert for
the catalog (confirmed on the deployed demo image). The STAC read path
(`Honua.Protocols.Stac.Services.StacV2Lookups`) only surfaces a collection when a
publication of type `stac-collection` exists on a service whose `protocols` include `Stac`,
backed by a resolvable resource + storage binding.

The seed handles all of this: it appends a STAC service + two `stac-collection`
publications (Maui Reef Watch `90810`, Maui Coastal Change `90820`), their resources and
storage bindings, into the **current active snapshot**, writes a new revision, and
activates it. Existing graph entities (the 11 `maui-*` layers) are preserved.

### Assets

- `tests/seed/demo-stac-imagery-v1.sql` — the idempotent seed (features + v2 graph merge).
- `tests/seed/apply-demo-stac-seed.sh` — psql wrapper.

### Run it

**[OPERATOR]** Set `HONUA_SEED_ENV` to the env id the demo Lambda is configured with —
this MUST match the server's `Metadata__Environment` / `Environment` setting (it defaults
to `default`; confirm against the Lambda's environment variables):

```bash
# Local / direct-psql target:
PGHOST=... PGPORT=5432 PGUSER=honua PGDATABASE=honua PGPASSWORD=... \
HONUA_SEED_ENV=default HONUA_SEED_SCHEMA=honua \
  tests/seed/apply-demo-stac-seed.sh
```

For `demo.honua.io` the DB is in-VPC only. Send the **entire** contents of
`tests/seed/demo-stac-imagery-v1.sql` (it is one transaction) as a single statement to the
bootstrap Lambda, with the `env`/`schema` psql vars pre-substituted (the file reads them
via `:'env'` / `:"schema"`; when going through the Lambda, replace those tokens with the
literal env/schema before sending, or wrap the body so `set_config('honua.seed_env', ...)`
is set first):

```bash
# [OPERATOR] example: invoke the bootstrap Lambda with the seed body
aws lambda invoke --function-name honua-demo-demo-postgis-bootstrap \
  --payload "$(jq -Rs '{query: .}' < tests/seed/demo-stac-imagery-v1.sql)" \
  --cli-binary-format raw-in-base64-out /dev/stdout
```

The seed is **idempotent**: re-running advances the snapshot revision and re-applies the
two collections without duplicating features or publications.

### Verify (REQ-001)

```bash
curl -s https://demo.honua.io/stac/collections | jq '.collections | length'   # >= 2
curl -s https://demo.honua.io/stac/collections | jq -r '.collections[].id'     # 90810, 90820
curl -s -X POST https://demo.honua.io/stac/search \
  -H 'content-type: application/json' \
  -d '{"bbox":[-156.70,20.60,-156.30,20.96],"collections":["90810"]}' \
  | jq '.features | length'   # > 0
```

Acceptance: Imagery & Terrain Studio (`demo-imagery-terrain.html`) shows a live STAC
catalog instead of the bundled sample lane.

---

## Track 2 — Apply a Pro license (REQ-002/003/004/005)

A Pro license unblocks the three remaining Pro-gated demo areas. Entitlement keys
(from `FeatureCatalog`):

| Demo area            | Entitlement key                       | Min edition |
|----------------------|---------------------------------------|-------------|
| Realtime streaming   | `streaming.feature-subscriptions`     | Pro         |
| FeatureServer edits  | `editing.featureserver-edits`         | Pro         |
| Forward geocoding    | `geocoding.forward`                   | Pro         |
| Reverse geocoding    | `geocoding.reverse`                   | Pro         |
| Geocoding failover   | `geocoding.failover`                  | Pro         |

> Editing note: only the **Esri GeoServices FeatureServer** write surface
> (`applyEdits`) is Pro (`editing.featureserver-edits`). Editing via the **open
> protocols** (OGC API Features mutations, WFS-T, OData CRUD/`$batch`, gRPC) is
> Community and ungated. The editing demo exercises the FeatureServer write path, so it
> needs the Pro license.

### Step 1 — Mint the Pro license (offline, publisher-only) **[OPERATOR]**

Use the in-repo `honua-license-mint` CLI (`src/Honua.LicenseMint`). The signing key is the
trust root — **never commit it**.

```bash
# (a) one-time: generate the signing key pair
dotnet run --project src/Honua.LicenseMint -- keygen \
  --key-id honua-demo-2026q2 \
  --private-out ./honua-demo-2026q2.private    # chmod 600; store in Secrets Manager only

# (b) mint a Pro envelope (defaults to all entitlements at/below Pro)
dotnet run --project src/Honua.LicenseMint -- mint \
  --key-id honua-demo-2026q2 \
  --license-id lic-honua-demo-2026q2 \
  --licensed-to "Honua Demo (demo.honua.io)" \
  --edition Pro \
  --expires 2027-06-15T00:00:00Z \
  --private-key-file ./honua-demo-2026q2.private \
  --out ./honua-demo-license.json
```

`keygen` prints the runtime trusted-key setting:
`Licensing__TrustedKeys__honua-demo-2026q2=<base64url>`. Record it for Step 3.

> Per #1688 recon, a Pro envelope + signing key may already be staged in Secrets Manager
> (`honua-demo-demo/license`, `honua-demo-demo/license-signing-key`) with keyId
> `honua-demo-2026q2`. Reuse those rather than re-minting if present.

### Step 2 — Store the envelope in Secrets Manager **[OPERATOR]**

```bash
aws secretsmanager put-secret-value \
  --secret-id honua-demo-demo/license \
  --secret-string file://honua-demo-license.json --region us-west-2
```

Do **not** put the license envelope or signing key in the repo, in Terraform state, or in
client pages.

### Step 3 — Wire the license onto the demo Lambda **[OPERATOR]**

`Licensing:LicenseContent` accepts either the raw envelope JSON **or** a secret reference
(`aws:secretsmanager:<arn>`) which is resolved at startup — prefer the secret reference so
no secret material lands in the Lambda config:

```
Licensing__LicenseContent=aws:secretsmanager:arn:aws:secretsmanager:us-west-2:585192672263:secret:honua-demo-demo/license
Licensing__TrustedKeys__honua-demo-2026q2=base64url:<public-key-from-keygen>
```

`LicenseContent` takes precedence over `LicensePath`, so this works on the read-only
Lambda filesystem with no bootstrap file write.

### Step 4 — Enable geocoding (resolve the 404, REQ-003) **[OPERATOR]**

Geocoding is published when `Geocoding:Enabled=true` with a configured provider and locator
name. The demo probes `/rest/services/maui/GeocodeServer`, so set the locator name to
`maui` (or publish under the default `World` and update the demo probe). On the demo
Lambda:

```
Geocoding__Enabled=true
Geocoding__LocatorName=maui
Geocoding__DefaultProvider=nominatim     # or the configured provider
# plus provider config (endpoint/key) per Geocoding__Providers__*
```

The forward/reverse geocoding **operations** are Pro-gated (`geocoding.forward` /
`geocoding.reverse`), so the Pro license from Steps 1–3 must be active.

### Step 5 — Streaming + editing need no extra config

`streaming.feature-subscriptions` and `editing.featureserver-edits` are unlocked purely by
the active Pro license. Streaming additionally requires Redis (already a server health
prerequisite) for the change-feed/replay backend. The editing demo writes to a writable
OData/FeatureServer layer with CORS/If-Match/ETag already fixed (#1629/#1653).

### Step 6 — Redeploy and verify

`terraform apply` (or update the Lambda env + `update-function-code`) and re-run the
probes:

```bash
# REQ-004 streaming
curl -s https://demo.honua.io/api/v1/streaming/features/capabilities \
  | jq '{enabled, edition}'                       # {"enabled": true, "edition": "Pro"}

# REQ-003 geocoding
curl -s 'https://demo.honua.io/rest/services/maui/GeocodeServer?f=json' \
  | jq '.currentVersion'                          # present (no 404)
curl -s 'https://demo.honua.io/rest/services/maui/GeocodeServer/findAddressCandidates?f=json&singleLine=Kahului' \
  | jq '.candidates | length'                     # > 0

# Edition / capability manifest
curl -s https://demo.honua.io/api/v1/capabilities/manifest \
  | jq '.license.edition'                          # "Pro"

# REQ-005 editing — authenticated PATCH/POST to a writable layer should persist
#   (round-trips on re-read). NFR-001: write creds stay server-side, never in client pages.
```

Acceptance (#1688): Public Safety Ops connects a live incident feed; dispatch geocoding
resolves live; Inspection & Editing persists an edit; the four probes pass from the
`honua.io` origin.

---

## End-to-end validation

With both tracks applied, re-run the SDK smoke from `honua-sdk-js`:

```bash
node scripts/site-demo-smoke.mjs    # the four affected pages leave their fixture/replay lanes
```

## Runnable now vs. needs live env / creds

| Item                                          | State                                   |
|-----------------------------------------------|-----------------------------------------|
| STAC seed SQL + apply script                  | **Runnable now** (validated vs PostGIS) |
| `release-packages` 42P08 fix + tests          | **Merged in this PR**                   |
| License mint CLI commands                      | Runnable; signing key is **[OPERATOR]** |
| Apply license / geocoding env on demo Lambda  | **[OPERATOR]** — live env + secrets     |
| Redeploy + probe                               | **[OPERATOR]** — live env               |
