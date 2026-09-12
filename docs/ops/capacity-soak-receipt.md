# Candidate capacity-soak receipt

The release train will not certify a candidate without a capacity/SLO receipt. This page
describes the producer that makes one: what it measures, on what substrate, what the receipt
claims, and how anyone can verify a published receipt for themselves.

- Producer: [`.github/workflows/capacity-soak-candidate.yml`](../../.github/workflows/capacity-soak-candidate.yml)
- Substrate: [`docker-compose.soak.yml`](../../docker-compose.soak.yml)
- Measurement: [`scripts/soak/`](../../scripts/soak/)
- Consumer: honua-release `.github/workflows/capacity-soak.yml` →
  `tools/check_capacity_soak.py --lock certification/capacity-envelope.v1.json`
- Contract: honua-release `docs/CAPACITY-ENVELOPE-2026.1.md` and the frozen
  `certification/capacity-envelope.v1.json`

## The chain

1. The release train takes a `capacity_receipt_url`.
2. Its `capacity-soak.yml` gate fetches that HTTPS JSON, runs
   `gh attestation verify --repo honua-io/honua-server` over the fetched bytes, and evaluates
   them against the frozen lock.
3. This workflow is what produces those bytes, signs them, and publishes them at a URL the
   gate can fetch anonymously.

Run it with `workflow_dispatch`, passing the manifest-pinned honua-server SHA from
honua-release's `platform-manifest.yaml`:

```bash
gh workflow run capacity-soak-candidate.yml -R honua-io/honua-server \
  -f candidate_sha=<40-char sha> -f lock_ref=trunk
```

`steady_state_seconds` defaults to the lock's `soak.minimumSteadyStateSeconds` (3,600) and is
refused below it. `candidate_image` runs a published image instead of building the candidate
source; either way the receipt's `observedRevision` is read back out of the running server, so
an image that is not the candidate fails the run rather than producing a mislabelled receipt.

## Substrate and scope (operator ruling A, 2026-09-12)

The soak runs on the **local-docker** substrate: `docker compose` on a GitHub-hosted runner
with PostGIS, Redis and the candidate server image, under the **Production** startup policy
(the run fails if the server reports any other environment). No cloud substrate is exercised,
and the receipt says so in its `substrate` block. A capacity claim derived from this receipt
is a claim about this topology.

The deployment runs on a real, signed **Pro** licence minted for the run (`Honua.LicenseMint`),
because `Licensing:DevGrantEdition` is refused under the Production startup policy by design and
the declared envelope covers Pro surfaces. The key pair is generated on the runner, used once,
and never leaves it.

## What the run does

1. Builds (or pulls) the candidate image and boots the substrate.
2. Waits for `/healthz/ready` under the Production startup policy.
3. Seeds **exactly** the declared envelope topology — one service, `layersPerService` layers,
   `featuresPerLayer` features each — with [`scripts/soak/seed_envelope.py`](../../scripts/soak/seed_envelope.py),
   then re-reads it from the database.
4. Recompiles the served metadata-v2 snapshot from the seeded catalogue and restarts the server
   (the snapshot is compiled at startup, so a catalogue seeded afterwards is otherwise invisible).
5. Runs the lock's `soak` profile at the declared concurrency for the locked steady state, while
   [`scripts/soak/drive_soak.py`](../../scripts/soak/drive_soak.py) observes the signals a request
   generator cannot see.
6. Runs a recovery drill **after** the steady-state window closes.
7. Builds the receipt, self-checks it with the same rules the release gate applies, attests it,
   publishes it, and then re-verifies the **published** bytes exactly as the gate will.

### Seeding order is the fix for #3812

`tests/seed/server.yaml` is the migration-**skipping** fixture: it creates the migration-owned
core schema itself. Applying it before a Production server boots leaves those tables present
with no row in `public.schema_versions`, and `PostgresCoreSchemaGuard` fails closed at migration
003 (`SchemaExistsWithoutJournal`, `raster_layer_statistics`). The server exits, `/healthz/ready`
never answers, and the lane dies at "Wait for readiness" — which is what
`load-soak-nightly.yml` did every night from 2026-09-02.

The server owns the core schema. It migrates first; data is seeded afterwards. That is the order
`.github/actions/setup-honua-server` already used, it is the order `load-soak-nightly.yml` now
uses, and this producer seeds data only.

## The eight signals

Every signal carries its own `method`, `unit`, window, sample count and supporting evidence in
the receipt. A measurement that could not be taken is recorded without a value and fails the run;
nothing is defaulted.

| Signal | How it is measured |
|---|---|
| `availability` | served/attempted ratio of an independent 1 Hz FeatureServer query probe across the steady-state window — deliberately not the load harness's own counters, so availability and error rate are two observations rather than one number reported twice |
| `errorRate` | failed/total requests from the NBomber soak run |
| `p95LatencyMs`, `p99LatencyMs` | worst-scenario percentile across the profile's scenarios, the same worst-scenario reading the frozen limits were taken from |
| `throughputRps` | successful requests ÷ full run duration (ramp-up + steady + ramp-down), the aggregate the frozen floor was derived from |
| `queueAgeSeconds` | maximum age a queued geoprocessing job reached before the single declared worker started it, sampled once per second |
| `saturationRatio` | peak connection-pool utilisation from the server's own `/monitoring/metrics/connection-pool`; a sample without utilisation data invalidates the signal |
| `recoveryTimeSeconds` | seconds from injecting a server-process fault (container restart) until the deployment served a feature query again, measured after the steady-state window so the frozen availability budget is not spent on a deliberate outage |

## Envelope coverage

`check_capacity_soak.py` compares the receipt's `envelope` block with the lock's
`supportedEnvelope` for exact equality. Copying a block is easy; claiming it is not. The receipt
therefore also carries `envelopeVerification` — one record per declared dimension, with what was
declared, what was observed, and how — and an `envelopeCoverage` summary. A dimension is either

- `verified` — established on the deployment under test and re-observed there, or
- `not-exercised` — declared by the lock and deliberately not driven by this run, with the reason
  recorded in the receipt.

There is no third, silent state: a dimension with neither record fails receipt construction.

### Known not-exercised dimension: `alertEvaluationsPerSecond`

The alert pipeline validates at startup that tenant-context resolution is **off** ("alert
evaluation and delivery stores are instance-wide"). With `MultiTenancy:Enabled=false`, the OGC
API Features item query — which the soak profile drives and through which the declared envelope's
layers are served — answers `403 Tenant context is required to query collection items`. Driving
the declared alert rate and serving the declared envelope are therefore mutually exclusive on one
deployment, so the soak keeps the serving surface and records the alert rate as declared, not
claimed.

### Note on `gpQueueDepth`

There is no queue-capacity setting to read back: the declared depth is an operating bound. With
the declared single worker, execution admission answers `503 Global active job limit reached
(1/1)` to a submission that arrives while a job runs, so the queue does not grow towards the
bound through the GPServer submit path. What the run verifies is that geoprocessing work ran
continuously under the declared single-worker configuration and the observed queue never exceeded
the bound; the admission rejections are recorded as evidence.

## Signing, publication and verification

`signingIdentity` and `signature` are not self-asserted text:

1. the payload — the receipt without its signature members, canonicalised as sorted-key JSON with
   no insignificant whitespace — is attested with `actions/attest-build-provenance`, which signs
   an in-toto statement whose subject digest is the payload's SHA-256, under a Fulcio certificate
   issued to this workflow's identity;
2. [`scripts/soak/sign_receipt.py`](../../scripts/soak/sign_receipt.py) checks that the bundle
   really covers those bytes, then copies the DSSE signature into `signature` and the
   certificate's SAN into `signingIdentity`;
3. the finished receipt is attested as well — that is the attestation the consumer verifies.

To verify a published receipt yourself:

```bash
curl --fail --silent --show-error --proto '=https' --tlsv1.2 "$RECEIPT_URL" -o receipt.json
gh attestation verify receipt.json --repo honua-io/honua-server
python3 tools/check_capacity_soak.py --lock certification/capacity-envelope.v1.json \
  --receipt receipt.json --expected-revision "$(yq '.components.honua-server.sha' platform-manifest.yaml)"
```

(the last two commands from a honua-release checkout). To re-derive the signed payload, drop the
`signature`, `signingIdentity`, `signatureFormat` and `signingIdentitySource` members and
re-serialise with sorted keys and `(',', ':')` separators; its SHA-256 is the subject digest named
in `signatureFormat`.

### Why a commit-pinned raw URL

The consumer fetches with `curl --fail --silent --show-error --proto '=https' --tlsv1.2` and no
`-L`. A GitHub **release-asset** URL answers `302`, so that curl would write an empty file and
exit 0 — release assets are not a usable publication target for this gate. A
`raw.githubusercontent.com` URL pinned to a commit answers `200` with the exact bytes, needs no
credentials, and cannot be moved afterwards. Receipts are committed to the orphan `soak-receipts`
branch of this repository — the same repository whose attestation the gate verifies — so
publishing evidence never touches trunk.

## A failing soak still publishes a receipt

If a signal misses its frozen threshold, the run still builds, signs and publishes the receipt —
an honest negative one — and then asserts that the real gate **refuses** it. The workflow fails.
Thresholds are never relaxed to make a receipt pass, and a threshold miss is reported as a
finding against the candidate.
