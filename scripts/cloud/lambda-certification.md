# Lambda GA certification lane

`lambda-preview-certification.yml` certifies the manifest's exact image digest and
`awsLambdaArchitecture` (`x86_64` → OCI `amd64`, or `arm64`). It preserves the true
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
- `verification.coldStartInitDurationMs`: observed first-invoke REPORT value.
- `serving.result` and `serving.candidateDigest` (digest only).
- `serving.deployed`, `.baseline`, `.candidate`, `.rollback`: migration assertions;
  fixture name/hash, expected/actual row count and name verification; created,
  read-back, deleted and remaining row counts; distinct write target; denial
  principal/operation/expected and actual status/zero records, anonymous 401; executed version.
- `serving.alias.beforeVersion`, `.afterVersion`, `.rollbackVersion`.
- `serving.teardown.candidateVersionDeleted`, `.standingLatestRestored`.

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
teardown failure. The separately built driver compiles the unchanged production
backend/client. Actual AWS IAM, VPC/PostGIS connectivity, fixture bootstrap,
cold-start behavior and serving across real published versions still require the
credentialed workflow run.
