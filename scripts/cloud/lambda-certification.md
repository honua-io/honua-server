# Lambda GA certification lane

`lambda-preview-certification.yml` certifies the manifest's exact image digest and
`awsLambdaArchitecture` (`x86_64` → OCI `amd64`, or `arm64`), using the matching
native GitHub runner for the executable runtime-adapter check. It preserves the true
ECR digest handoff and the existing image revision, runtime adapter, health,
CloudWatch, ownership and teardown checks. The ECR mirror is a release output,
as in the original lane; functions, log groups, written rows and the candidate
published version are temporary.

The lane is fail-closed. A failed or absent serving check produces `serving.result:
noProof` and a nonzero exit; it never treats missing live inputs as a skipped pass.
The workflow serializes with `real-aws-certification` because both use the standing
certification alias. No AWS credentials are needed for the offline self-test.

## Cert bootstrap inputs

Keep the existing image/revision/repository/execution-role inputs. Supply:

| Input | Contract |
| --- | --- |
| `architecture` dispatch input | Copy `awsLambdaArchitecture` from the same manifest candidate as the digest. Both source and ECR platforms must match. |
| `REALAWS_CERT_LAMBDA_FUNCTION` repo variable | Standing `honua-cert-cert-*` image function, with cert PostGIS configured, migrations enabled and private VPC subnets/security groups. |
| `REALAWS_CERT_LAMBDA_ALIAS` repo variable | Standing published, unweighted certification alias. |
| `REALAWS_CERT_LAMBDA_WRITE_BASE_URL` repo variable | Function URL belonging to that exact alias; verified through AWS before any writes. |
| `HONUA_DEMO_BASE_URL` repo variable | Demo read URL. Matching write/read hosts, including case, port and trailing-slash variants, are refused. |
| `REALAWS_CERT_DENIED_KEY` cert secret | **Optional override, deprecated.** The lane mints its own scoped `read:layers` principal per run (see below). For one release, explicitly selecting the workflow input `use_denied_key_override: true` sends this key instead and mints and revokes nothing. An existing secret alone does not select the override. Remove the secret once this release has shipped. |
| `REALAWS_CERT_ADMIN_KEY` cert secret (optional, one release) | Temporary override, mapped to `HONUA_LAMBDA_CERT_ADMIN_KEY`. A nonempty override takes precedence over Secrets Manager; leave it unset to follow credential rotation automatically. Never stored in evidence. |

By default, preparation reads `HONUA_ADMIN_PASSWORD` from the standing function's
configuration and calls `secretsmanager:GetSecretValue` for that reference before
mirroring or creating resources. References use `aws:secretsmanager:<name-or-ARN>`,
with optional `?versionStage=<stage>&versionId=<id>` URI-escaped selectors, matching
the server resolver. An ARN selects its own region; a name uses `AWS_REGION`.
The admin secret must contain the password as a nonempty `SecretString` (or UTF-8
`SecretBinary`). Missing, malformed, inline or unreadable references fail closed
when no override is set.

The selected value is registered with GitHub Actions log masking before use as
`x-api-key`. It stays in process memory and private, temporary invocation payloads;
it is never exported to job outputs, receipts or artifacts. The serving process
resolves again from the configuration captured during preparation and caches the
value for that process. Outside Actions, no mask command containing a key is
printed. The explicit override is masked and checked against the denied principal
in the same way. Remove the override after this release; leaving a copied override
set retains the rotation drift risk.

The cert OIDC role needs `secretsmanager:GetSecretValue` on that exact secret via
honua-iac's existing `CertificationStackSecretsRead` grant (honua-iac #175).
Access denial stops the lane with the required resource ARN: a configured ARN is
reported directly; a name becomes
`arn:<partition>:secretsmanager:<region>:<account>:secret:<name>-??????` (only the
six-character AWS suffix is wildcarded). Report that diagnostic to the iac owner;
do not widen policy or trust in this lane. Raw AWS errors and secret values remain
suppressed. A secret rotation during a run, or an alias version whose frozen
configuration names a different secret, can still fail serving assertions and
requires checking that configuration before retrying.

The ephemeral function inherits the standing function's PostGIS connection/secret
reference, authentication configuration and VPC attachments. Its execution role
must already permit those VPC attachments and resolution of the cert secrets.
No IAM policies or trust are changed by this lane. Bootstrap must also already
permit code update/publish, version reads/deletion, alias URL reads, and the
production deploy backend's alias SDK calls on the standing cert function, and
`lambda:UpdateFunctionConfiguration` on the run-namespaced `honua-certrun-lambda-*`
function — that is how the lane forces the cold execution environment it certifies
(see below), and it is the one AWS action the lane did not previously call.

The referenced `real-aws-certification.yml` contains control-plane tests, and
`aws-cert/ecs-alb-cert.tf` runs nginx behind an internal ALB; neither seeds a Honua
serving fixture. The PostGIS reuse therefore comes from the standing Honua Lambda,
which shares the certification VPC. Bootstrap that cert database with the existing
`tests/seed/client-compat-v1.sql` snapshot (the same fixture used by
`docker/client-compat/seed/run.sh`) from a runner with private database reachability.
This is a cert bootstrap prerequisite, not a permission to reset standing data.
It asserts all ten names and the exact count on `test_service/0`, and uses the
snapshot's scratch layer `test_service/10` for only its run-owned row. Missing or
drifted fixture data fails the run.

### The fixture applies to a database the server has already migrated

The cert database is **not** the fresh database `docker/client-compat` creates. The standing
cert function runs with migrations enabled, so by the time bootstrap applies the snapshot the
schema is whatever the server's DbUp migration set produced. The snapshot must be applicable
to that migrated shape, and it is the migrated shape that it declares:

- The snapshot's `CREATE TABLE IF NOT EXISTS` / `ADD COLUMN IF NOT EXISTS` DDL is a no-op
  against a migrated database — the migrated definitions win. Those definitions must therefore
  mirror `src/Honua.Server/Migrations`, or the same fixture name yields one schema on the fresh
  `docker/client-compat` path and a different one in certification.
- The snapshot's INSERTs must name only columns the migrations create.
  `honua.services.max_record_count` was exactly this divergence: the snapshot declared and
  inserted it, no migration has ever created it, and the bootstrap failed with
  `42703 column "max_record_count" of relation "services" does not exist` before the lane could
  reach a single serving assertion. The server takes its paging cap from `Limits:Query`
  configuration and, under Metadata v2, from the service settings slot, so the column carried
  no fixture meaning; it was dropped from the snapshot rather than reintroduced into the cert
  database.
- Every statement stays re-appliable (`IF NOT EXISTS`, `ON CONFLICT`, `WHERE NOT EXISTS`). The
  standing cert database is re-primed between runs, so a second application must converge
  rather than fail.

`ClientCompatSeedMigratedDatabaseTests`
(`tests/dotnet/Honua.Server.Tests/Seed/ClientCompatSeedMigratedDatabaseTests.cs`) pins this
contract: it migrates a PostGIS container with the production DbUp runner
(`PostgresDatabaseMigrationRunner` over the server migration assembly), applies the snapshot
twice over the result, and asserts the ten names and the `test_service/10` add/delete through
the FeatureServer query path.

Two properties of the serving assertions are environment, not fixture, and the standing cert
function has to supply them:

- **The lane is an authenticated principal.** `invoke()` defaults to `authenticated=True` and
  sends the resolved admin credential on every serving call except the two explicit denial probes.
  The snapshot's `allowAnonymous` policy is what makes the *denial* probes meaningful; it is not
  what admits the fixture reads or the scratch-layer writes.
- **`test_service/10` writes need a licensed function.** FeatureServer edits are gated on the
  Pro entitlement `editing.featureserver-edits` (`FeatureServerEditsHandler`). An unlicensed
  (Community) function refuses `addFeatures` and the lane records `serving.result: noProof` — a
  licensing gap, not fixture drift. Certify against a function whose license carries that
  entitlement: on AWS that is honua-iac's `enable_pro_license`, which injects
  `Licensing__LicenseContentSecretRef` and `Licensing__TrustedKeys__<keyId>`.

  The refusal does **not** arrive as an HTTP 402. GeoServices reports every operation failure as
  HTTP 200 with the whole error in the body, so the entitlement gate reaches the lane as
  `{"error":{"code":402,...}}` and the assertion line reads `status=200 expected=200`. Run 28
  (34320738962) failed exactly this way, with `error=402` and nothing else, which is why the lane
  now echoes the server's `message` and `details` on every in-body GeoServices failure and answers
  a 402 with a `serving-402:` line naming the entitlement, the license variable (by name), and the
  server's own edition and validation state:

  ```text
  serving-assertion: phase=deployed path=/rest/services/test_service/FeatureServer/10/addFeatures status=200 expected=200 body-kind=json error=402 message=Payment Required details=FeatureServer Editing requires an active Pro entitlement. ... :: entitlement: editing.featureserver-edits
  serving-402: phase=deployed entitlement=editing.featureserver-edits variable=Licensing__LicenseContentSecretRef presence=absent source=none trusted-keys=0 edition=Community validation=NoLicenseConfigured entitled=false
  ```

  `presence=absent` is a stack that was never given a license; `presence=present` with a
  `validation` other than `Valid` is an envelope the server refused. They have different owners.

### The command the substrate uses to apply it

The cert database is reachable only from inside the certification VPC, so bootstrap applies the
snapshot through the honua-iac `postgis-bootstrap` Lambda's maintenance `statements` mode
(`infrastructure/terraform/examples/aws-cert/postgis-bootstrap/handler.py`). That handler runs
each element of the payload's `statements` array through a single
`pg8000.native.Connection.run()` call over the extended query protocol, so the caller sends the
snapshot split into top-level statements — split on semicolons outside string literals,
comments and dollar-quoted bodies, which keeps the snapshot's `honua.seed_metadata_v2_compat_snapshot()`
body intact:

```bash
# statements.json: {"statements": ["CREATE EXTENSION ...", "CREATE SCHEMA ...", ...]}
aws lambda invoke \
  --function-name "$CERT_POSTGIS_BOOTSTRAP_FUNCTION" \
  --cli-binary-format raw-in-base64-out \
  --payload file://statements.json \
  bootstrap-response.json
```

There is no `psql` anywhere in that path. The snapshot must contain no backslash meta-commands
(`\i`, `\copy`, `\set`) and nothing that depends on a client-side splitter beyond top-level
semicolons.

## Byte-exact ECR mirror

Lambda runs the ECR copy, so the artifact ECR stores must be the artifact the platform
manifest pins — not a re-encoding of it. The lane mirrors with `crane copy` by digest
(pinned and checksum-verified in the workflow), which uploads the source manifest and its
blobs verbatim. A `docker pull` / `docker tag` / `docker push` round trip cannot be used:
the daemon re-serialises the image config through its own representation, which changes the
config blob digest and makes ECR's copy a different artifact.

The verification is therefore blob identity, never envelope identity:

- ECR re-encodes the OCI manifest into a Docker schema 2 envelope, so its **manifest**
  digest legitimately differs from the source's and is never compared.
- The **config blob digest** and the **layer blob digests** must match the source manifest
  exactly, and the rootfs `diff_ids` the pulled ECR image declares must match the source
  image's. Any mismatch fails the run (exit 4).
- When the pinned source is a multi-platform index, the lane resolves the one
  `linux/<candidate architecture>` child and mirrors that manifest. Zero or more than one
  matching child fails the run (exit 3) rather than guessing.
- A tag written by an earlier re-encoding mirror can never be accepted by a later run.

The mirror tag is `candidate-<revision:12>-<source digest:12>-<architecture>`. The architecture is
part of it because the pinned source digest may name a multi-platform index: certifying the same
revision and pin for `arm64` and for `x86_64` mirrors two different child manifests, and a shared
tag would make each run read the other's certified artifact as a stale mirror and delete it.

The certification repository is **tag-immutable**, so a rerun for the same candidate cannot
overwrite the tag a previous attempt wrote — the manifest `PUT` is rejected with `TAG_INVALID`.
The lane therefore decides what to do with an existing tag before it pushes, and records the
decision in `artifact.mirrorOutcome`:

| Tag state | `mirrorOutcome` | Action |
| --- | --- | --- |
| Absent | `pushed` | `crane copy`, as before. |
| Present, exact source artifact | `skipped-existing` | No push. The verification below still runs in full against what ECR holds. |
| Present, anything else | `replaced-stale` | `aws ecr batch-delete-image` on that tag, then `crane copy`. |

"Exact source artifact" is blob identity — the stored config blob digest and layer blob digests,
or a manifest digest equal to the source's — never envelope identity, for the reason above.
The ECR copy is a mirror whose source of truth is the GHCR pin, so a tag holding anything else is
a stale mirror artifact and is replaced rather than trusted; the delete is confined to the
`honua-cert-cert-lambda-preview` repository and a `candidate-*` tag, and refuses anything outside
that namespace (exit 95). A `DescribeImages` failure that is not `ImageNotFoundException` fails the
run (exit 3) rather than being read as an absent tag; a `BatchGetImage` failure, or a stored
manifest without exact config and layer digests, likewise fails the run (exit 3) rather than
reading as a blob mismatch, because a lookup that failed is not evidence that the tag holds a stale
artifact and must never be the reason a prior run's artifact is deleted. A stale tag that could not
be removed fails the run (exit 4) rather than being left for the verification to accept. Bootstrap must permit
`ecr:DescribeImages` and `ecr:BatchDeleteImage` on that repository.

## The certified invoke has to be made cold, not assumed cold

Run 18 (`34203568834`) exited 14 at the cold-start check with
`tail-has-init-report=0 tail-has-report=1`: the invoke's `REPORT` carried no `Init Duration`,
no `INIT_REPORT` existed to fall back to, and the serving assertions never ran.

Neither shape of "a previous run left something warm behind" explains it, and neither is
possible in this lane:

- **There is no pre-existing published version to land on.** The evidence invoke targets
  `honua-certrun-lambda-<run id>-<attempt>`, a function this run creates and tears down; the lane
  refuses to start at all if that name already exists (exit 93). Nothing is published or aliased
  until step 4, which runs *after* certification. Run 17 also certified a different source digest
  (`0b526ccb…`, against run 18's `f11bfdc9…`), so it left neither a function nor a mirror tag that
  run 18 could have reused.
- **Nothing invokes the function between create and the evidence invoke.** That invoke is the
  first `aws lambda invoke` of the run, and it addresses the function, not the standing alias.

What actually happened is that the environment was already initialized before the invoke reached
it. **Lambda proactively initializes an execution environment while a newly created function
transitions to `Active`**, so `wait function-active-v2` followed by an invoke is not a cold start —
it is a race, and which side wins is set by how long activation takes:

| Run | Source digest | Mirror verified → step outcome | Outcome |
| --- | --- | --- | --- |
| 17 (`34118866591`) | `0b526ccb…`, deployed to Lambda by earlier runs | 29 s, and by then it had already *failed a serving assertion* | Activation, the evidence invoke and the first serving calls all fit in half a minute: the invoke beat the ~21 s initialization, so the cold start was observed and the run got past this check |
| 18 (`34203568834`) | `f11bfdc9…`, first Lambda deployment of that digest | 4 min 19 s to exit 14, of which at least 2 min is the lane's own bounded polling | Activation was slow — the platform had not yet cached an optimized copy of this image — so the initialization finished inside it and the invoke landed on the completed environment |

That also accounts for the earlier `INIT_REPORT ... Phase: invoke` runs (13 and 15): there the
proactive initialization lost the race, the invoke did its own initialization, the ~21 s needed to
resolve secrets and open the database over the VPC blew Lambda's init window, and the runtime
re-ran it inside the invoke. Nothing about the queries was wrong in run 18 — there was no
initialization inside the invocation for the tail *or* CloudWatch to report.

Two changes make the evidence robust without touching the assertion:

- **The lane forces the environment it certifies.** A configuration change discards every execution
  environment a function holds, so before each evidence invoke the lane writes an inert nonce
  (`HONUA_LAMBDA_CERT_COLD_START=<run token>-<n>`) into the cloned standing environment, waits for
  the update to settle, and invokes immediately — putting the initialization ahead of the invoke
  instead of behind it. The nonce never stands in for the artifact: `Code.ResolvedImageUri` is
  re-read after every update and must still be the mirrored digest (exit 5). Because proactive
  initialization can win again, the lane makes up to three such attempts, each with a new nonce and
  therefore a new environment, and says so in the job log when one comes back warm.
- **The CloudWatch query follows the environment, not a window around the invoke.** One log stream
  is one execution environment, so the lane resolves the stream its invoke ran in (from the
  delivered request id) and searches *that* stream from the log group's own creation. The group is
  created by this run and deleted at teardown, so the widened range is still entirely this run's.
  The previous `invoke − 120 s` window was a second race with the same cause: an activation slower
  than two minutes — exactly the slow-image-optimization case — put anything pre-invoke outside it.
  Delivery of the platform lines lags the request id by minutes, so the search is a bounded poll,
  and an empty answer is never read as an absent cold start until the poll is spent.

  The search is scoped to that one stream and is never widened back to the group, because forcing
  environments is exactly what puts *other* attempts' streams in it: a group-wide read could credit
  one attempt's late-delivered `INIT_REPORT` to a later attempt's invoke, and the receipt would
  carry an `Init Duration` and a request fingerprint from two different invocations. For the same
  reason every attempt proves delivery of its *own* request id before reading any evidence — the
  receipt records the last attempt's fingerprint, so `cloudWatchLogsVerified` has to be about that
  invoke — and that delivery is also what names the stream, so the two waits are one bounded poll.

The assertion itself is unchanged and still fail-closed: a passing receipt carries a positive
`Init Duration` that this run observed, from a `REPORT` line or from a non-error `INIT_REPORT`.
Three forced environments that all come back warm fail the run (exit 14), and the failure now
reports the attempt count alongside the tail shape.

## The denial principal is minted per run, not carried by the bootstrap

Certification run 23 (`34243173689`) reached the authorization assertion for the first time — cold
start, admin authentication and the `client-compat-v1` fixture all passed — and failed it with
`Scoped principal must receive an empty HTTP 403 (zero records)`.

The key it sent came from `REALAWS_CERT_DENIED_KEY`, minted at bootstrap on 2026-09-06.
Server-managed API keys live in Redis when an eligible multiplexer is registered; otherwise they
live in process-local memory (`Program.cs`, `IAdminApiKeyStore` registration). They are not PostGIS
rows. Loss of that store could cause a 401; run 23 did not log the returned status, so neither a
missing key nor a 200 authorization leak is confirmed.

The default path requires Redis configuration on both standing `$LATEST` and the published alias.
After minting, it also reads the exact active key and its effective permissions through the standing
alias before serving assertions begin. This verifies shared visibility at runtime, including when
Redis is configured but not eligible and the server falls back to an in-memory store. A visibility
failure retires the candidate's key and leaves `noProof`.

For direct shell runs, the deprecated override requires both `HONUA_LAMBDA_CERT_DENIED_KEY` and
`HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE=true`. Explicitly requesting an override without a key
fails before provisioning.

The lane already holds the administrator, so it does not have to depend on a credential some
earlier bootstrap left behind. It mints its own instead:

- Before the first serving assertion, the lane creates `honua-cert-denied-<run id>-<attempt>`
  through `POST /api/v1/admin/api-keys` with exactly the `read:layers` grant — the genuinely scoped,
  non-admin principal of `AdminApiKeyEndpointsTests.GenuinelyScopedApiKey_IsDeniedAdminEndpoint` —
  and asserts the record came back active with those permissions and no others.
- Every phase (`deployed`, `baseline`, `candidate`, `rollback`) sends that key for the denial
  assertion. All four must share the Redis API-key store, so one record serves the whole run —
  including the baseline and rollback phases, which validate the candidate's key with the
  *previous* published version's code. A key that authenticates in one phase and not another is an
  API-key compatibility break between those two versions, and the diagnostic below names the phase.
- The mint carries a two-hour expiry, because an abandoned credential is a standing one and a run
  can die between the mint and the revoke.
- The API exposes revocation rather than physical deletion. Teardown revokes the key by its unique
  name — which also recovers the row a lost create response left
  behind — then re-reads the record and requires the server's own view of it to be `revoked` with
  `canAuthenticate: false` and no active record of this run's name remaining. Revocation is
  attempted against the candidate function first and the standing alias second, so a candidate that
  cannot serve is not also the run that leaves a credential behind. A key that could not be retired
  fails the run even when every serving assertion passed.

No new AWS permission is involved: the mint and the revoke are ordinary administrative requests to
the function under test, sent the same way as every other serving assertion.

### A denial that is not the documented 403 has to say which way it failed

Run 23 printed neither the status nor the shape of what it got, so the run could not distinguish
its two opposite causes: the scoped key was gone (401), or the server served admin records to a
non-admin principal (200 — honua-server#4386). A failed denial assertion now prints:

```
serving-403: phase=deployed principal=override key=HONUA_LAMBDA_CERT_DENIED_KEY status=401 body-kind=json authenticated=no challenge=ApiKey+Basic records=0 record=unknown
```

| Field | What it says |
| --- | --- |
| `principal` / `key` | `minted` and this run's key name, or `override` and the *name* of the variable the key came from. Never key material. |
| `status` / `body-kind` | The status actually returned, and whether the body was the documented empty one, a JSON document, or text. |
| `authenticated` | Inferred from the endpoint's status contract: `no` for 401, `yes` for 403 or 200, `unknown` otherwise. This separates a missing key from a leak without claiming a separate authentication probe. |
| `challenge` | The scheme that issued the `WWW-Authenticate` challenge, parsed as in the 401 diagnostic above; `none` when absent. |
| `records` | How many records the answer carried. Zero for the documented refusal; anything else is a leak, counted rather than quoted. |
| `record` | For a minted key, the server's own status for that record (`active`, `revoked`, `expired`, `missing`); `unknown` for an override, because no plaintext key can be mapped back to its row. |

`authenticated=no` on a minted key means the credential the lane just created was refused by the
function under test, which is an authentication defect, not a bootstrap gap. `authenticated=yes`
with a nonzero `records` is the authorization leak the assertion exists to catch.

## A Lambda function error has to say which side failed, and how

Lambda reports an initialization failure, a handler exception and a timeout identically at the API:
HTTP 200 with `FunctionError` set. The reason is only in the invocation's response payload, and the
platform's own account of it is only in the log tail. Live run 25 (34305710517) — the first run on
the Redis-enabled cert stack — stopped on nothing but `Lambda invocation failed`, and the receipt
was the only thing that said where: `deniedKey.created` and `deniedKey.revoked` were both true and
`sharedStoreVerified` was false, so the candidate had served well enough to mint this run's key and
to revoke it, and the invocation that failed was the *first invoke of the standing alias*. Neither
that, nor init-versus-handler, was anywhere in the job log.

Every invoke on both stages now reports through one classifier:

```
serving-invoke: phase=denied-key-shared-store target=standing-alias path=/api/v1/admin/api-keys/<id>/effective-permissions status=200 executed-version=7 function-error=Unhandled kind=init error-type=Runtime.ExitError error-message=Error: Runtime exited with error: exit status 134
serving-invoke-log: INIT_REPORT Init Duration: 7412.55 ms Phase: init Status: error Error Type: Runtime.ExitError
```

| Field | What it says |
| --- | --- |
| `phase` | The lane phase the invocation belongs to, as in every other serving diagnostic; `cold-start-evidence` for the shell stage's own `/healthz/live` invokes. |
| `target` | **Which side of the certification failed**: `candidate` or `standing-alias`. Never a function name — the standing function is a fingerprint everywhere else in this evidence. It is not simply "was the name qualified": `serve()` shifts the standing alias to the newly published candidate version, so for the `candidate` phase the alias *is* the candidate. The version Lambda reports it executed decides the attribution, and the phase answers when the invoke never reached a version. |
| `status` / `executed-version` / `function-error` | What the invoke API itself answered. `status=204` with no version is the dry-run answer the ninth live run got; `function-error=none` with a non-200 status is an API-level failure rather than a failing function. |
| `kind` | `init` when the platform's `INIT_REPORT` ended in error or timeout, or the error type is an `Init*` one — the function never reached the handler. `timeout` when the runtime reported the invocation ran out of time. `handler` when the function started and threw. `runtime-exit` when the runtime *process* died (`Runtime.ExitError`, `Runtime.ExitCode`) and the tail did not establish the phase — Lambda raises those during an invocation as readily as during initialization, so the lane says the phase is unestablished rather than guessing one. `unknown` when Lambda returned no error document at all. |
| `error-type` / `error-message` | From the runtime's own error document, redacted and capped exactly as every other echoed diagnostic: the message is dropped whole rather than filtered down to a fragment if either runtime key shows through it. |
| `serving-invoke-log:` | The last **platform-authored** lines of that invocation's own log tail — `--log-type Tail` already carries it back with the response, so an initialization failure's report is in hand without a CloudWatch query, a delivery wait, or a permission on another function's log group. The tail also carries the application's own stdout, which is never echoed: `HONUA_ADMIN_PASSWORD` reaches the function as an `aws:secretsmanager:` reference, so the password the server resolves per request is a value no redaction set in the lane can hold. |

`target` is the field to read first. `target=candidate` is a defect in the artifact under
certification — including in the `candidate` phase, where the artifact is reached through the
standing alias. `target=standing-alias` is not: the candidate is not what failed, and the cert
stack itself has to be repaired before any run can produce a proof.

Redaction for this diagnostic covers more than the two keys the lane holds. The lane clones the
standing function's whole environment onto the candidate, so an inline connection string or token
it never chose can come back inside a server-authored message; every cloned value long enough to
be a credential joins the comparison set, as it already does for the shell stage's
`create-function error:` reporter. Declared references (`aws:secretsmanager:<arn>`, `env:<name>`)
are excluded: they are pointers the lane already reports publicly by kind, and treating them as
secrets would drop every line that so much as names Secrets Manager — exactly the line a failure
resolving a secret would print.

## An administrative 401 has to say which of its causes it is

The lane authenticates as the bootstrap administrator: it sends the resolved credential (or the temporary override) as
`x-api-key`, and `ApiKeyAuthenticationHandler` compares that against whatever `HONUA_ADMIN_PASSWORD`
resolves to on the function under test — a Secrets Manager reference the handler re-resolves per
request in the AWS serverless configuration. The lane clones that configuration and never injects a
credential of its own, so there is exactly one way in and three ways to lose it:

- the deployed environment carries no `HONUA_ADMIN_PASSWORD` at all;
- it carries the reference, but the handler cannot turn it into a usable password this request —
  the execution role lost read access to the secret, the secret resolves empty, or the refreshed
  value fails `AdminPasswordValidation` in a Production environment. `ResolveAdminPasswordAsync`
  catches that and fails the request; or
- it resolves to a password the lane's key no longer equals — because an explicit override is
  stale, the secret rotated during the run, or the alias version names a different reference.

All three are HTTP 401, all three are the admin Problem Details document, and all three carry the
title `Unauthorized`. Run 21 (34222614774) failed its first serving assertion with nothing but that
title, and separating them took the standing configuration and a manual probe.

Two things close that gap. Preparation refuses a standing environment whose `HONUA_ADMIN_PASSWORD`
is missing or blank **by name**, before anything is mirrored or created, because such a run can only
end in 401. And any 401 on a serving assertion now prints a second line naming the credential
variable, whether the *deployed* function carries it (`present`/`absent`/`unreadable`, never its
value), whether it is a Secrets Manager reference or an inline value, the scheme that issued the
challenge as parsed from the response's own `WWW-Authenticate`, and the server's fixed refusal
detail:

```
serving-assertion: phase=deployed path=/api/v1/admin/observability/migrations status=401 body-kind=json error=Unauthorized
serving-401: variable=HONUA_ADMIN_PASSWORD presence=present source=secretsmanager-reference challenge=ApiKey+Basic detail=API key required. Provide a valid API key in the X-API-Key header.
```

`presence` and `detail` together name the cause, and they are what an operator should read before
touching any credential:

| `presence` | `detail` | Cause | Action |
| --- | --- | --- | --- |
| `absent` | `Admin authentication not configured` | The deployment has no administrator. | Restore the variable in the cert stack; do **not** rotate anything. |
| `present` | `Admin authentication not configured` | The reference is there but did not resolve to a usable password: execution-role access, an empty secret, or a production complexity failure. | Fix the IAM grant or the secret's contents. Rotating the bootstrap key changes nothing. |
| `present` | `API key required.` | The function has an administrator; this key is not it. | Remove a stale `REALAWS_CERT_ADMIN_KEY` override; check rotation timing and the published version’s reference, then rerun. |

`presence` is read from the function actually invoked, which for the alias phases is the published
version's own frozen environment rather than `$LATEST`. Every echoed field is a name or a
server-authored constant, capped and character-filtered, and dropped outright if either runtime key
shows through. That comparison runs against the unfiltered text as well as the filtered one, and
treats any run of twelve consecutive key characters as the key: filtering and truncation are exactly
what would otherwise leave a key behind as a normalized or truncated fragment that no longer matches
it.

## A GeoServices refusal has to say what the server refused, and why

The Esri GeoServices contract answers **every** operation failure with HTTP 200 and the failure
only in the body: `{"error":{"code":N,"message":...,"details":[...]}}`. A serving assertion that
reads the status alone therefore reads a refused edit as a pass, and one that reads the status and
the code alone cannot separate an invalid geometry from an unknown layer, a read-only layer, a
missing required field, a schema that drifted under a migration, or a surface this deployment is
not licensed for. They are all `error=<code>` on the same `status=200 expected=200` line.

Run 28 (34320738962) is what that costs. Everything before the run-owned write passed — cold-start
evidence, admin authentication, the client-compat-v1 fixture assertion, the per-run scoped key
minted through the candidate and visible to the standing alias, the empty-403 denial — and the run
stopped here:

```
serving-assertion: phase=deployed path=/rest/services/test_service/FeatureServer/10/addFeatures status=200 expected=200 body-kind=json error=402
```

The `serving-assertion:` line now carries the server's own `message` and its `details` array on any
in-body GeoServices error, joined and bounded under the same redaction as every other echoed
diagnostic. A `402` — the entitlement gate, from either the body code or the HTTP status — also
gets a second line, because that one code has two opposite owners:

```
serving-assertion: phase=deployed path=/rest/services/test_service/FeatureServer/10/addFeatures status=200 expected=200 body-kind=json error=402 message=Payment Required details=FeatureServer Editing requires an active Pro entitlement. Current edition is Community install a license that includes editing.featureserver-edits. :: entitlement: editing.featureserver-edits
serving-402: phase=deployed entitlement=editing.featureserver-edits variable=Licensing__LicenseContentSecretRef presence=absent source=none trusted-keys=0 edition=Community validation=NoLicenseConfigured entitled=false
```

| Field | What it says |
| --- | --- |
| `entitlement` | `editing.featureserver-edits` when the refused path is a GeoServices write operation (`addFeatures`, `updateFeatures`, `deleteFeatures`, `applyEdits`, `calculate`) — the entitlement `FeatureServerEditsHandler` enforces once for the whole write surface. `none` on any other path, which is `LicenseOperationMiddleware` refusing the **deployment** license outright rather than one gated surface. Those are different owners, so the lane never claims an entitlement for a whole-deployment block. |
| `variable` / `presence` / `source` | The license envelope variable **by name** on the function actually invoked, whether it is there at all, and whether it is a Secrets Manager reference the server resolves at startup or an inline value. Never the envelope. |
| `trusted-keys` | How many `Licensing__TrustedKeys__*` variables the function carries. A licensed function with no trusted key cannot verify the signature it was given. |
| `edition` / `validation` / `entitled` | The server's **own** verdict, read from `/api/v1/admin/license/status` on the same deployment that just refused the write. `LicenseOperationMiddleware` lets the license routes through even when the deployment license itself is blocked, so this answers whatever the license state is. |

| `presence` | `validation` | Cause | Action |
| --- | --- | --- | --- |
| `absent` | `NoLicenseConfigured` | The cert stack was never given a license, so the function is Community and FeatureServer editing is gated. | Enable honua-iac `enable_pro_license` on the cert stack (`pro_license_content` + `pro_license_trusted_public_key`, or `pro_license_secret_arn`) and re-apply. Nothing about the fixture or the lane is wrong. |
| `present` | anything but `Valid` | The function was handed an envelope the server refused: an unresolvable secret reference, a missing or wrong trusted key, an expired license. | Fix the envelope or the trusted key in the cert stack; the fixture and the lane are still not the cause. |
| `present` | `Valid`, `entitled=false` | The license is valid and does not carry this entitlement. | Re-issue a license whose entitlements include `editing.featureserver-edits`. |

A 402 carrying `entitlement=none` is not in that table at all: the deployment's license is
unusable and **every** data route is being refused, which the lane meets on its first
administrative read rather than at the scratch-layer write. Renew or restore the license itself.

The entitlement is enforced for the whole GeoServices write surface, so on an unlicensed
deployment the lane's own cleanup `deleteFeatures` is refused after the `addFeatures` that failed:
expect the pair of assertions, not one. Nothing was written, so nothing is left behind.

`ClientCompatSeedMigratedDatabaseTests` pins both halves of this locally: over one migrated PostGIS
container and this exact seed, the lane's exact `addFeatures` payload is refused with HTTP 200 and
body code 402 by an unlicensed host and accepted by a licensed one. The seed's layer 10, the
migrated schema and the lane's payload are therefore not what a 402 is reporting.

## Live proof

1. Mirror and verify the digest, clone the standing cert environment/VPC, and boot
   an ephemeral function. Force a fresh execution environment, then keep that invoke's
   `REPORT` request ID and positive `Init Duration` in milliseconds, and verify the
   invocation reached CloudWatch. See "The certified invoke has to be made cold, not
   assumed cold" above for why the first invoke of a fresh function is not enough.
2. Require migration status `succeeded`, ready, no failure, available plan, no
   upgrade and zero pending scripts. Query exactly ten named fixture records.
3. Require an anonymous principal's `GET /api/v1/admin/api-keys` to return the
   documented admin Problem Details 401 with zero records, and this run's own scoped
   `read:layers` principal to receive HTTP 403 with an empty body (zero records).
   Create one uniquely
   named feature, read its ID and value through the API, delete it, and verify
   absence. An ambiguous create response also triggers marker-scoped cleanup.
4. Prove the baseline alias serves. Update only standing `$LATEST` code and publish
   a version tagged by description with this run ID. The lane driver compiles the
   repository's **actual** `AwsLambdaGitOpsDeployBackend` and `AwsLambdaAliasClient`
   sources and calls plan/start/observe, then rollback/observe. It does not issue a
   raw CLI alias update or change any production source.
5. Repeat serving assertions on candidate and rollback alias versions, asserting
   `ExecutedVersion` on every invoke. Restore the original `$LATEST` image, verify
   the alias baseline, and delete only the newly published owned version once no
   aliases reference it. Failed serving assertions still attempt these restorations.
6. Delete the tagged ephemeral function and its log group and verify absence.

The function URL binds the write target identity. API Gateway v2 events are sent
with the AWS Invoke API directly to the ephemeral function or qualified cert
alias, so requests do not depend on public ingress or redirect behavior.

## Receipt additions

`evidence/lambda-preview-receipt.json` keeps its existing schema and fields and adds:

- `deployment.architecture`: asserted manifest architecture.
- `artifact.sourcePlatformDigest`: the resolved single-platform source manifest digest
  (equal to `artifact.sourceDigest` unless the pin named an index).
- `artifact.sourceConfigDigest`, `artifact.sourceRootfsFingerprint`, `artifact.mirrorTool`,
  `artifact.configDigestPreserved` and `artifact.rootfsPreserved`: the byte-exactness proof.
- `artifact.mirrorOutcome`: `pushed`, `skipped-existing` or `replaced-stale` — what the mirror step
  did about the immutable candidate tag.
- `verification.coldStartEvidenceSource`: `tail` when the invoke's own log tail carried the cold-start line,
  `cloudwatch` when it was read back from the function's log group after delivery was verified (the
  4 KB tail does not always reach the INIT_REPORT line).
- `verification.coldStartInitDurationMs` and `verification.coldStartInitPhase`: the observed
  first-invoke Init Duration and the phase that carried it (`init` from the REPORT line, or
  `invoke` from the INIT_REPORT line when initialization exceeded Lambda's init window and
  was re-run inside the first invoke).
- `verification.coldStartEnvironmentForced` and `verification.coldStartInvokeAttempts`: that the
  certified invoke ran on an execution environment this run forced into existence, and how many
  forced environments it took before one was actually cold. Both are asserted in the receipt.
- `serving.result`, `serving.candidateDigest` (digest only), and `serving.candidateVersion`.
- `serving.deployed`, `.baseline`, `.candidate`, `.rollback`: migration assertions;
  fixture name/hash, expected/actual row count and name verification; created,
  read-back, deleted and remaining row counts; distinct write target; denial
  principal/operation/expected and actual status/zero records, anonymous 401; executed version.
- `serving.deployed.authorization.principalSource`: `minted` for this run's own key, `override`
  when the deprecated bootstrap secret supplied it.
- `serving.deniedKey`: `source`, the granted `permissions`, this run's key `name` (never its
  value), whether it was `created`, whether teardown `revoked` it, the server's own
  `canAuthenticate` for the revoked record, and `activeAfterTeardown` — how many records bearing
  this run's name were still active when the lane finished, which a passing receipt requires to be
  zero. `sharedStoreVerified` records the standing alias's visibility of the minted key before
  serving begins. An `override` run creates and revokes nothing.
- `serving.alias.beforeVersion`, `.afterVersion`, `.rollbackVersion`.
- `serving.teardown.candidateVersionDeleted`, `.standingLatestRestored`.

Failed rollback preserves observed versions and cleanup flags in a `noProof`
receipt. A version still referenced by an alias is never deleted; that failed run
requires operator recovery of the standing alias before the owned version can be
removed. An ordinary serving failure restores routing and still deletes its version.

No endpoint URLs, connection strings, API keys or raw AWS logs enter the receipt.
Infrastructure identifiers remain fingerprints. A pass from the offline stubs is
not a live certification receipt and must not be used for manifest admission.

## Offline verification

```bash
python3 scripts/cloud/test-certify-lambda-preview.py
dotnet build scripts/cloud/lambda-deploy-driver/LambdaDeployDriver.csproj --configuration Release
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj --configuration Release --filter FullyQualifiedName~LambdaAotDockerfileTests
```

The self-test runs the full shell/Python lane with stateful AWS CLI, container and
deploy-driver doubles. It covers pass on both architectures, every assertion,
missing inputs, URL guards, lost shift/publish responses, rollback failure and
teardown failure. The doubles model ECR tag immutability — a second `crane copy` to an
occupied tag is rejected — so the absent / same-digest / different-digest rerun cases and
their fail-closed variants are exercised end to end. They also model proactive initialization
(`STUB_PROACTIVE_INIT` is how many forced environments Lambda pre-initializes), per-stream delivery
of the platform lines, and CloudWatch delivery lag (`STUB_CLOUDWATCH_LAG`), so the retry that
reaches a cold environment, the fail-closed run where none of them is ever cold, the stream-scoped
poll that outwaits the lag, and the warm attempt whose stream must not supply a later attempt's
evidence are all covered offline; an unscoped evidence read fails the doubles outright. The doubles
carry a stateful admin API-key store, so the per-run mint, the denial, the revoke and the receipt's
`activeAfterTeardown` proof run end to end, together with a key that no longer authenticates (401),
one the server serves records to (200), a refused mint, a mint whose response is lost and whose row
teardown still has to find by name, and a revocation that fails and fails the otherwise-passing run. The separately built driver compiles the unchanged production
backend/client. Actual AWS IAM, VPC/PostGIS connectivity, fixture bootstrap,
cold-start behavior and serving across real published versions still require the
credentialed workflow run.
