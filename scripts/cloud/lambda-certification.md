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
| `REALAWS_CERT_DENIED_KEY` cert secret | Valid, pre-existing scoped API key with only `read:layers`, as in `AdminApiKeyEndpointsTests.GenuinelyScopedApiKey_IsDeniedAdminEndpoint`. It must authenticate but lack admin rights. |
| `REALAWS_CERT_ADMIN_KEY` cert secret | Admin key for the standing certification function and its cloned configuration. Never stored in evidence. |

The ephemeral function inherits the standing function's PostGIS connection/secret
reference, authentication configuration and VPC attachments. Its execution role
must already permit those VPC attachments and resolution of the cert secrets.
No IAM policies or trust are changed by this lane. Bootstrap must also already
permit code update/publish, version reads/deletion, alias URL reads, and the
production deploy backend's alias SDK calls on the standing cert function.

The referenced `real-aws-certification.yml` contains control-plane tests, and
`aws-cert/ecs-alb-cert.tf` runs nginx behind an internal ALB; neither seeds a Honua
serving fixture. The PostGIS reuse therefore comes from the standing Honua Lambda,
which shares the certification VPC. Bootstrap that cert database with the existing
`tests/seed/client-compat-v1.sql` snapshot (the same fixture used by
`docker/client-compat/seed/run.sh`) from a runner with private database reachability.
This is a cert bootstrap prerequisite, not a permission to reset standing data.
The lane does not run the seed's schema/data updates against an existing database.
It asserts all ten names and the exact count on `test_service/0`, and uses the
snapshot's scratch layer `test_service/10` for only its run-owned row. Missing or
drifted fixture data fails the run.

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
run (exit 3) rather than being read as an absent tag, and a stale tag that could not be removed
fails the run (exit 4) rather than being left for the verification to accept. Bootstrap must permit
`ecr:DescribeImages` and `ecr:BatchDeleteImage` on that repository.

## Live proof

1. Mirror and verify the digest, clone the standing cert environment/VPC, and boot
   an ephemeral function. Keep the first invoke's `REPORT` request ID and positive
   `Init Duration` in milliseconds, and verify that invocation reached CloudWatch.
2. Require migration status `succeeded`, ready, no failure, available plan, no
   upgrade and zero pending scripts. Query exactly ten named fixture records.
3. Require an anonymous principal's `GET /api/v1/admin/api-keys` to return the
   documented admin Problem Details 401 with zero records, and a valid scoped
   principal to receive HTTP 403 with an empty body (zero records). Create one uniquely
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
- `verification.coldStartInitDurationMs`: observed first-invoke REPORT value.
- `serving.result`, `serving.candidateDigest` (digest only), and `serving.candidateVersion`.
- `serving.deployed`, `.baseline`, `.candidate`, `.rollback`: migration assertions;
  fixture name/hash, expected/actual row count and name verification; created,
  read-back, deleted and remaining row counts; distinct write target; denial
  principal/operation/expected and actual status/zero records, anonymous 401; executed version.
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
their fail-closed variants are exercised end to end. The separately built driver compiles the unchanged production
backend/client. Actual AWS IAM, VPC/PostGIS connectivity, fixture bootstrap,
cold-start behavior and serving across real published versions still require the
credentialed workflow run.
