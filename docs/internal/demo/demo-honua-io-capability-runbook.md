# demo.honua.io full-capability runbook (server#1688)

Operator runbook to bring `demo.honua.io` to full demonstrable capability across the
four product pillars. It covers two separable tracks:

1. **Seed STAC collections** so the Imagery & Terrain Studio shows a live catalog.
2. **Apply a Pro license** so geocoding failover, realtime streaming, and
   FeatureServer editing light up. Single-address forward/reverse geocoding is
   available in Community; batch geocoding requires Enterprise.

> Scope note: the underlying server features already exist. This is **operational
> enablement** on the deployed demo. The only server code change shipped alongside this
> runbook is a fix for the `release-packages` admin API (see *Prerequisites*).

Account context (from #1688 recon): AWS `585192672263`, `us-west-2`, compute is Lambda
`honua-demo-demo-honua`; the demo DB is reachable only in-VPC via the bootstrap Lambda
`honua-demo-demo-postgis-bootstrap` (accepts `{"statements":[...]}` / `{"query":"..."}`).

Everything below is **operator-supplied where it touches secrets or the live env** —
those steps are marked **[OPERATOR]**. No secrets are committed in this repo.

---

## As-verified live state (2026-07-23, server#2948)

A fresh, read-only probe of `https://demo.honua.io` on 2026-07-23 found this runbook's
Tracks 1 and 2 **already applied and serving correctly**, plus an elevation/terrain
dataset that the 2026-07-20 probe (server#2948) reported missing but which is in fact
live. The table below is the current source of truth; the track sections further down
are kept for the mechanism/history but where they disagree with this table, this table
wins.

| Area | State | Evidence |
|---|---|---|
| Track 1 — STAC seed | **Live.** `/stac/collections` returns `90810` + `90820`; `/stac/search` against `90810` returns 4 features; both collections also appear in `/api/v1/streaming/features/capabilities` layer list. | Direct probe, 2026-07-23 |
| Track 2 — Pro license | **Live.** `/api/v1/capabilities/manifest` → `policies.currentEdition = "Pro"`, `licenseValidationState = "Valid"`. Community `geocoding.forward`/`geocoding.reverse` and Pro `geocoding.failover`/`streaming.feature-subscriptions`/`editing.featureserver-edits` all report `active: true`; `geocoding.batch` correctly remains Enterprise. The 14/29 `available:false` capability entries seen on an **anonymous** manifest call are RBAC (`insufficient-policy`, expected with no caller identity) or correctly `minimumEdition: Enterprise` gates (we're Pro) — not a licensing gap. | Direct probe, 2026-07-23 |
| Elevation / terrain | **Live and fully functional**, contradicting the 2026-07-20 probe. `maui-terrain` (layerId 8) is a registered raster dataset; `/elevation/maui-terrain/{value,profile,viewshed,line-of-sight,sun-shadow}` all return real computed results over real Maui terrain (elevations in a sane 4–590 m range), and `/terrain/maui-terrain/tile.json` + `.png` tiles serve `terrain-rgb` encoded tiles. No remediation needed. | Direct probe, 2026-07-23 |
| Catalog cleanup — layer 68823 | **Done.** Not present in `/rest/services` (25 services, none named `test_service`/68823) or `/ogc/features/collections` (11 collections). Direct probe of `/rest/services/test_service/FeatureServer` returns `499 Unauthorized` (`allowAnonymous: false`), consistent with the recorded remediation (`PUT /api/v1/admin/services/test_service/access-policy {"allowAnonymous": false}`). | Direct probe, 2026-07-23 |
| Geocoding | **Not working — categorical failure, not a cold-start.** The locator is published as `World` (not `maui`, which the original probe and this runbook's Step 4 assumed — `GET /rest/services/World/GeocodeServer?f=json` returns valid metadata with `Provider: nominatim`). But every `findAddressCandidates` call — first and repeated — fails after ~15.8s with `500 "Geocoding service error"`. Two consecutive calls both took ~15.8s (15.790s, 15.854s): this rules out a cold-start/keep-warm fix. Root cause (per the 2026-07-20/21 coordinator note, reconfirmed): the demo Lambda's VPC subnets have no NAT/IGW egress route, so the external Nominatim endpoint is unreachable from inside the VPC on every call. **Operator-decided fix (2026-07-23): switch to Amazon Location Service via a VPC interface endpoint (PrivateLink) — no NAT gateway.** IaC in `honua-io/honua-iac#127` (not applied); see *Remediation plan* item 1. | Direct probe, 2026-07-23 |
| SensorThings | **Confirmed intentional.** `/sta/v1.1/*` → 404. `Experimental:Features:SensorThings` defaults `false` in `src/Honua.Server/appsettings.json` with no environment override, and there is no SensorThings entry in the capabilities manifest at all (not even a gated one). This matches honua-io/honua-server#2434 ("Promote SensorThings to GA" — `roadmap:later`, still experimental). No action needed for this issue; GA promotion is tracked separately in #2434. | Direct probe + code read, 2026-07-23 |
| `/api/scenes` (adjacent, server#2991) | **Fixed live.** Returns `200` with the scene listing — confirms the out-of-band migration run (073→082) documented in #2991 has landed on the shared demo DB. | Direct probe, 2026-07-23 |
| ImageServer `maui-imagery` metadata (adjacent, server#2991/#2993) | **Still hangs** (>70s, no response) — expected: the code fix is in PR #2993, which has not been deployed to the demo image yet. Will be resolved by the next demo image redeploy (see remediation plan). | Direct probe, 2026-07-23 |

**Deployment model correction (learned 2026-07-23, applies to every step below that
touches Lambda env or the DB):** `demo.honua.io` serves a **published Lambda version**
via the `live` alias (currently **v33**), and that published version's **environment is
frozen** — editing environment variables on `$LATEST` has **no effect on what's
actually serving traffic**. The only way DB-side changes (seeds, migrations) reach the
live site is because they mutate the **shared RDS database**, which every Lambda
version/alias reads from — so a DB write done by invoking `$LATEST` directly *does*
show up on `demo.honua.io` even though `$LATEST` itself never serves a request. An
**environment variable** change (e.g. `Geocoding__LocatorName`, `Licensing:LicenseContent`)
does **not** reach `demo.honua.io` this way — it only takes effect once a **new Lambda
version is published and the `live` alias is repointed to it** (i.e. a real deploy).
This is why Track 1 (a DB write) and Track 2 (apparently an env change, but see below)
show up as live today while an env-only fix would not have. The out-of-band DB
migration procedure used in #2991 (invoke `$LATEST` directly, which runs
`HONUA_SKIP_MIGRATIONS=false`-equivalent startup migrations against the shared DB, then
leave `$LATEST`'s config untouched) is the template for any future schema-only fix; it
does **not** work for anything that must be visible to the *serving* environment
(license content, geocoding locator/provider config) — those need an actual versioned
deploy.

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

> Open `https://demo.honua.io/healthz/live`, `https://demo.honua.io/healthz/ready` in a browser.

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

> **Do not derive the metadata environment from `server.deploymentEnvironment`.**
> The manifest field reports `IWebHostEnvironment.EnvironmentName` (`Production` on
> the demo), while the metadata graph independently reads `Metadata__Environment`,
> then `Environment`, with a `default` fallback. Operator records say the successful
> 2026-07-20/21 seed apply used `HONUA_SEED_ENV=Production`, and the live catalog
> confirms those rows are active, but the manifest alone does not prove that mapping.
> Before any repeat apply, inspect `Metadata__Environment` / `Environment` on the
> serving Lambda version or query the active environment in `metadata_v2_current`.

**[OPERATOR]** Set `HONUA_SEED_ENV` to the env id the demo Lambda is configured with —
this MUST match the server's `Metadata__Environment` / `Environment` setting (it defaults
to `default`; confirm against the serving Lambda's environment variables or the active
`metadata_v2_current` row — the capabilities manifest's host-environment field is not
the metadata environment):

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

Open `https://demo.honua.io/stac/collections` in a browser and confirm it contains at least two collections, including `90810` and `90820`. Then use the [API explorer workflow](../../reference/openapi-and-explorer.md) for `POST /stac/search` with `{"bbox":[-156.70,20.60,-156.30,20.96],"collections":["90810"]}` and confirm the response contains features.

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
| Forward geocoding    | `geocoding.forward`                   | Community   |
| Reverse geocoding    | `geocoding.reverse`                   | Community   |
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

> **2026-07-23 status: the 404 is already resolved, but geocoding still doesn't work.**
> `Geocoding:Enabled=true` with `Provider: nominatim` is live today, published under the
> **default `World` locator** (`GET /rest/services/World/GeocodeServer?f=json` returns
> valid metadata) — the `maui` locator name this step originally called for was never
> applied and is not needed; **update any demo probe/smoke script to call
> `/rest/services/World/GeocodeServer`, not `/rest/services/maui/GeocodeServer`.**
>
> The real, still-open problem is downstream of the locator: every
> `findAddressCandidates` call — first or repeated — fails after a consistent ~15.8s
> with `500 "Geocoding service error"`. Two back-to-back calls both took ~15.8s
> (15.790s, then 15.854s on a supposedly "warm" second call), which rules out a
> cold-start explanation. Per the 2026-07-21 coordinator finding, reconfirmed
> 2026-07-23: **the demo Lambda's VPC subnets have no NAT gateway / internet gateway
> route**, so the external Nominatim endpoint is categorically unreachable from inside
> the VPC — not slow, unreachable, timing out at whatever the outbound HTTP client
> timeout is configured to (~15–16s). A scheduled keep-warm probe cannot fix this: it
> would just be another call that times out the same way.
>
> **Operator decision (2026-07-23): fixed via Amazon Location Service over a VPC
> interface endpoint (PrivateLink), not a NAT gateway.** No new compute, no general
> egress opened up. IaC: `honua-io/honua-iac#127` (place index + IAM grant +
> `com.amazonaws.us-west-2.geo.places`
> interface endpoint — not applied). See *Remediation plan* item 1 below for the exact
> env keys, sequencing, and rollback; a NAT-gateway + Nominatim fallback is kept at item
> 1a if Amazon Location is ever rejected later.

Geocoding is published when `Geocoding:Enabled=true` with a configured provider and locator
name. On the demo Lambda, this is already configured (Nominatim/`World`, currently
broken per the callout above):

```
Geocoding__Enabled=true
Geocoding__LocatorName=World              # confirmed live; do not change without also
                                           # updating every demo probe/smoke script
Geocoding__DefaultProvider=nominatim       # confirmed live; PLANNED to become
                                           # amazon-location — see Remediation plan item 1
# provider config (endpoint/key) per Geocoding__Providers__* — not independently
# re-verified here; the failure is at the network layer (see callout above), before
# provider request construction would matter.
```

The forward/reverse geocoding **operations** are Community capabilities
(`geocoding.forward` / `geocoding.reverse`), so they do not require the Pro
license from Steps 1–3. Automatic provider failover remains Pro
(`geocoding.failover`), while multi-address execution remains Enterprise
(`geocoding.batch`).

### Step 5 — Streaming + editing need no extra config

`streaming.feature-subscriptions` and `editing.featureserver-edits` are unlocked purely by
the active Pro license. Streaming additionally requires Redis (already a server health
prerequisite) for the change-feed/replay backend. The editing demo writes to a writable
OData/FeatureServer layer with CORS/If-Match/ETag already fixed (#1629/#1653).

### Step 6 — Redeploy and verify

`terraform apply` (or update the Lambda env + `update-function-code`) and re-run the
probes:

Open these demo URLs in a browser and inspect the named fields:

- `/api/v1/streaming/features/capabilities` — `enabled: true`, `edition: "Pro"`.
- `/rest/services/World/GeocodeServer?f=json` — `currentVersion` is present. The
  deployed locator is `World`, not `maui` (see the Step 4 callout).
- `/rest/services/World/GeocodeServer/findAddressCandidates?f=json&singleLine=Kahului`
  — `candidates` is non-empty once the VPC egress fix lands; as verified on
  2026-07-23, this request still returns 500 after about 15.8 seconds.
- `/api/v1/capabilities/manifest` — `policies.currentEdition` is `"Pro"`; the
  manifest has no top-level `license` object.

For REQ-005, make an authenticated edit with the `@honua/sdk-js` FeatureLayer
client and confirm it persists on re-read; keep write credentials server-side.

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
| STAC seed SQL + apply script                  | **Already applied and live** (2026-07-23 confirmed) |
| `release-packages` 42P08 fix + tests          | **Merged, live**                        |
| License mint CLI commands                      | N/A — Pro license **already applied and live** (2026-07-23 confirmed) |
| Elevation/terrain dataset (`maui-terrain`)    | **Already registered and fully working** (2026-07-23 confirmed) — no action needed |
| Layer 68823 catalog cleanup                   | **Already applied and live** (2026-07-23 confirmed) |
| Geocoding — Amazon Location + PrivateLink (PRIMARY) | IaC ready, **not applied**: `honua-io/honua-iac#127` — **[OPERATOR]** review/apply, then fold `Geocoding__*` env into the image redeploy below |
| Geocoding — NAT gateway + Nominatim (fallback, not chosen) | **[OPERATOR + honua-terraform]** only if Amazon Location is rejected later |
| SensorThings GA promotion                     | Out of scope for this issue — tracked in #2434 |
| Demo image redeploy (picks up #2993 + migrations 083-088) | **[OPERATOR]** — live env, standard versioned-alias deploy |

## Verification workflow (read-only, run against `https://demo.honua.io`)

Use the `honua` CLI for the protocol surfaces it supports. The `jq -e` assertions
fail closed if a response is missing or has the wrong shape.

```bash
set -euo pipefail
export HONUA_BASE_URL=https://demo.honua.io

# Track 1 — STAC
honua stac collections --json \
  | jq -e '([.collections[].id] | index("90810")) != null and
           ([.collections[].id] | index("90820")) != null'
honua stac search --bbox=-156.70,20.60,-156.30,20.96 \
  --collections 90810 --limit 25 --json \
  | jq -e '.features | type == "array" and length > 0'

# Catalog cleanup — test_service must not be publicly discoverable.
honua services --json \
  | jq -e '(.services | type == "array") and
           ([.services[].name | ascii_downcase] | index("test_service") | not)'

# Geocoding is the one known-broken CLI check until the VPC egress fix lands.
if time honua geocode "Kahului" --locator World --limit 1 --json \
  | jq -e 'type == "array" and length > 0'; then
  echo "OK: geocoding returned a candidate"
else
  echo "KNOWN: geocoding still fails after about 15.8 seconds; see remediation item 1"
fi
```

There is no released CLI command for capability manifests, elevation analysis,
SensorThings, scene catalogs, or ImageServer metadata. Do not invent one. Verify
those surfaces through their native clients:

- Read the deployment manifest with
  `HonuaControlPlaneClient.getCapabilityManifest()` from `@honua/sdk-js/control-plane`.
  Require `policies.currentEdition == "Pro"`, `licenseValid == true`, and
  `licenseValidationState == "Valid"`.
- Use `SceneView.elevationProfile()`, `SceneView.viewshed()`, and
  `SceneView.lineOfSight()` from `@honua/sdk-js/scene-workspace` for
  `maui-terrain`. Use the generated API explorer for point elevation and
  sun-shadow until typed SDK methods ship. The expected results are 10 profile
  samples, a numeric `visibleSampleCount`, a boolean `visible`, and a boolean
  `shadowCast`.
- Use `SceneView.listScenes()` for the scene-catalog regression check; it must
  return a scene list.
- Open `/sta/v1.1/` in a browser and confirm 404 (intentional, #2434).
- Open `/rest/services/test_service/FeatureServer?f=json` in a browser and
  confirm 499 or 403, never 200.
- Open `/rest/services/maui-imagery/ImageServer?f=json` in an ArcGIS-compatible
  client or browser. It is expected to time out before #2993 is deployed and to
  return metadata within the bounded budget afterward.

## Remediation plan for open items (operator approval required before any step below)

Everything in *As-verified live state* above marked "Live"/"Already applied" needs **no
further action**. The items below are the only ones still open as of 2026-07-23.

### 1. Geocoding — Amazon Location Service via PrivateLink (PRIMARY, operator-decided 2026-07-23)

**Decision:** the operator chose Amazon Location Service reached over a VPC interface
endpoint (PrivateLink) as the fix — **not** a NAT gateway. No new compute, no general
egress opened up; the server's built-in `amazon-location` geocoding provider
(`Honua.Geocoding.Features.Geocoding.Providers.AmazonLocationGeocodeProvider`) talks to
Amazon Location's classic Places API (`SearchPlaceIndexForText` /
`SearchPlaceIndexForPosition` / `SearchPlaceIndexForSuggestions`) against a named place
index, authenticating via the Lambda execution role (`UseIamRole=true`, the default —
no access keys). IaC PR (not applied): **honua-io/honua-iac#127** — adds an
`enable_amazon_location_geocoding` toggle to the `aws-serverless` module (place index +
least-privilege IAM grant) and wires a
`com.amazonaws.us-west-2.geo.places` interface endpoint
into the demo's `vpc-endpoints.tf` (single-AZ, same pattern as the existing Secrets
Manager/Bedrock endpoints — see that PR for the exact resources and a read-only AWS
audit of the account's current VPC/endpoint state, including one piece of *unrelated*
pre-existing drift it flagged: the live `bedrock-runtime` endpoint spans all 3 private
subnets despite being documented as single-AZ).

**Why this is the primary fix, not the NAT+Nominatim fallback below:** no NAT gateway
to provision or pay for (~$33/mo saved), no general internet egress opened on a public
demo Lambda, and it reuses a provider the server already ships — this is config +
one new AWS resource, not new infrastructure surface area.

**This requires the versioned-alias deploy below, not a standalone env change.** All of
the following are plain (non-secret) Lambda environment variables — per the
*Deployment model correction* above, environment variables only take effect on a
**newly published Lambda version with the `live` alias repointed to it**; there is no
way to apply them to what's actually serving `demo.honua.io` short of a real deploy.
Fold them into the same deploy that picks up PR #2993 (item 2 below) rather than
attempting a separate env-only change:

```
Geocoding__Enabled                                   = true
Geocoding__DefaultProvider                           = amazon-location
Geocoding__EnableFailover                            = false
Geocoding__Providers__Nominatim__Enabled             = false
Geocoding__Providers__AmazonLocation__Enabled        = true
Geocoding__Providers__AmazonLocation__Region         = us-west-2
Geocoding__Providers__AmazonLocation__PlaceIndexName = <honua-iac output: amazon_location_place_index_name>
Geocoding__Providers__AmazonLocation__UseIamRole     = true
Geocoding__Providers__AmazonLocation__MaxResults     = 10
```

`Geocoding__EnableFailover=false` is the effective safeguard against the existing
Nominatim timeout. The current server registration path always registers Nominatim for
backward compatibility, even when
`Geocoding__Providers__Nominatim__Enabled=false`; with failover enabled, the coordinator
would therefore still try it after any Amazon Location error (for example, a place-index
typo) and hang for another ~15.8 seconds. Keep the provider-specific flag false to record
the intended provider set, but do not rely on it until runtime registration honors that
flag. For normal requests that omit the optional `provider` query parameter, this demo
deployment deliberately attempts only Amazon Location. An explicit
`provider=nominatim` request can still select the registered Nominatim provider and
incur the unreachable-path timeout; demo smoke tests and clients must not send that
override. Re-enable failover only after every registered fallback has a working network
path.

**Sequencing:**
1. **[OPERATOR]** Review and apply `honua-io/honua-iac#127` (`terraform plan` then
   `apply`, scoped to `enable_amazon_location_geocoding = true` plus the other toggles
   this environment already runs) — creates the place index, the IAM grant, and the
   `com.amazonaws.us-west-2.geo.places` VPC interface endpoint. This alone does **not**
   change what's serving
   `demo.honua.io` (no Lambda env change yet, and the place index/endpoint are inert
   until referenced).
2. **[OPERATOR]** Fold the `Geocoding__*` env block above into the image-redeploy
   step (item 2 below) — same publish-new-version-and-repoint-alias operation, so the
   code fix (#2993), the migrations (083–088), and the geocoding provider switch all
   land in the one deploy that actually changes live behavior.
3. **Expected verification** (after the deploy in item 2 completes): open the
   `World` GeocodeServer metadata in the generated API explorer and confirm
   `locatorProperties.Provider` is `amazon-location`, then run:
   ```bash
   export HONUA_BASE_URL=https://demo.honua.io
   time honua geocode "Kahului" --locator World --limit 1 --json \
     | jq -e 'type == "array" and length > 0'
     # expect a non-empty candidate array in well under 1s
   ```
4. **Rollback:** the `Geocoding__*` env change rolls back the same way any other part
   of this deploy does — repoint the `live` alias back to the prior version (see item
   2's rollback). The place index and VPC endpoint from `honua-iac#127` are additive
   AWS resources with no coupling to STAC/license/catalog state; `terraform destroy
   -target` (or setting `enable_amazon_location_geocoding = false` and applying) removes
   them cleanly once nothing references them, but there is no urgency to tear them down
   just because the Lambda alias was rolled back — they cost nothing extra idle beyond
   the endpoint's fixed per-hour charge.

**Data-source note:** results come from **Esri** (the iac PR's default; `Here` is the
other option), not OpenStreetMap — this is a full provider swap, not a drop-in
replacement with identical results. Coverage, address formatting, and attribution
differ from Nominatim.

**Cost:** `com.amazonaws.us-west-2.geo.places` VPC interface endpoint ~$7–8/month
(single-AZ, same pricing as the
existing Secrets Manager endpoint) + Amazon Location's per-request Esri pricing tier
(low single dollars/month at demo traffic volumes). No NAT gateway (~$33/mo avoided),
no new compute.

### 1a. Fallback — NAT gateway + Nominatim (not the chosen path; kept for reference)

If Amazon Location is ever rejected (e.g. Esri/HERE data-licensing concerns, or the
place index proves unreliable), the fallback is what this runbook originally proposed:

- **NAT Gateway egress.** Add a public NAT Gateway with an Elastic IP in a public
  subnet whose route table sends `0.0.0.0/0` to the VPC Internet Gateway, then update
  each private Lambda subnet's route table to send `0.0.0.0/0` to that NAT Gateway (or
  use a correctly routed NAT instance, cost-dependent). Lambda ENIs remain in the
  private subnets; placing the NAT Gateway there, or merely adding an Internet Gateway
  route to those private subnets, does not provide internet egress —
  `enable_nat_gateway = true` in the `aws-serverless` module call handles this routing
  correctly, but a hand-rolled NAT setup should double-check both route-table hops.
  Costs ~$33/mo + data, the exact cost the Amazon Location path avoids.
- **Rollback:** additive (new NAT route); can be torn down without touching license,
  STAC, or catalog state.
- Once reachable, Nominatim itself is fast — the current ~15.8s is purely the egress
  timeout, not provider latency — so *only in this fallback path* would a scheduled
  keep-warm probe be a meaningful mitigation for genuine cold-start latency (it is not
  a fix for the current failure mode either way, which is unreachability, not
  slowness).

### 2. Demo image redeploy (picks up PR #2993 + migrations 083–088 + the Amazon Location env switch)

Routine versioned-alias deploy. See the *Deployment model correction* note above for
why this must be a real publish-and-repoint, not an env edit on `$LATEST` — and why
item 1's `Geocoding__*` env variables are folded into this same step rather than
attempted separately.

- **[OPERATOR]** Once PR #2993 merges to `trunk` and `honua-io/honua-iac#127` is
  applied (place index + `com.amazonaws.us-west-2.geo.places` endpoint exists): build
  and push a new demo image from
  `trunk` HEAD, set the `Geocoding__*` env block from item 1 above on the new version,
  publish it, and repoint the `live` alias from v33 to the new version
  (`aws lambda update-alias --function-name honua-demo-demo-honua
  --name live --function-version <new-version>` or the equivalent `terraform apply`).
- **Expected verification:**
  - `GET /rest/services/maui-imagery/ImageServer?f=json` returns within the 20s
    statistics budget (not a 70s+ hang).
  - A bare `GET /api/v1/tiles/pmtiles/maui-basemap` (no `Range` header) returns `413`
    with the byte-limit message, not an opaque 500.
  - `GET /api/v1/admin/observability/migrations` (authenticated) shows migrations
    through `088_CreateNetworkTopologyPromotions`.
  - Geocoding: see item 1's verification block (`locatorProperties.Provider ==
    "amazon-location"`, `findAddressCandidates` in well under 1s).
  - Everything else in the *As-verified live state* table above still holds (STAC, Pro
    license, elevation, catalog cleanup, SensorThings 404) — this is a regression
    check, not expected to change any of those.
- **Rollback:** repoint the `live` alias back to v33
  (`aws lambda update-alias --function-name honua-demo-demo-honua --name live
  --function-version 33`) — this reverts the geocoding provider switch along with
  everything else in the deploy, back to Nominatim/`World` (broken, as today) until a
  fixed version is published. No DB rollback needed — migrations 083–088 are additive
  (`CreateNetworkDataset*`/`CreateOpsHealth*`/network-topology tables) and unrelated to
  any table the demo currently reads from.

### 3. SensorThings

No action for this issue. `Experimental:Features:SensorThings=false` (default, no
override) is the intended state; GA promotion is tracked in #2434. Documented here to
close out this issue's acceptance criterion ("confirmed as intentionally gated ... and
recorded in the runbook").
