# Standing AWS certification plan-only workflow

The protected manual `standing-aws-cert-plan.yml` job runs on an ephemeral
GitHub-hosted Linux runner. It checks the reviewed IaC source, configured age
recipient, exact request bytes, pinned Terraform binary, provider lock and
complete VPC/RDS module bytes **before** asking GitHub for the existing `cert`
deployment role through OIDC. It then opens only the original remote backend
and existing workspace, checks the expected state lineage/serial, reads the
deployed PostGIS bootstrap ZIP and compares it to the reviewed SHA256 before
planning. It neither applies a plan nor creates a workspace or state backend.

The job encrypts the private packet to the reviewed age recipient and uploads
only `plan.age` and `receipt.json` under the exact run/attempt artifact name.
Its `always()` cleanup removes the owned IaC checkout, transient request,
tfvars, backend configuration, state cache, plan, diagnostics, bootstrap ZIP,
and local export. The hosted VM is ephemeral; this workflow makes no local
retention or recovery claim. The [IaC encrypted-packet guide](https://github.com/honua-io/honua-iac/blob/trunk/docs/devops/standing-cert-encrypted-packets.md)
defines authenticated exact-run private receipt and retention.

The workflow is **not ready for a live dispatch** until the protected `cert`
environment contains these reviewed inputs:

- The public `honua-iac` checkout uses the job's default read-only
  `GITHUB_TOKEN`. Checkout is fixed to its current `trunk`; the dispatch SHA
  must match that exact checkout. The workflow does not select an arbitrary
  revision.
- `HONUA_CERT_REQUEST_JSON`: exact v2 request bytes, SHA256 supplied in the
  dispatch. The request pins candidate, account, backend identity, existing
  state lineage/serial, image digests, secret references, separate backend and
  deployment roles, provider lock, exact IaC SHA, and the reviewed age recipient
  fingerprint. `privateStaging.directory`, `varFile` and `backendTokenFile` must
  use the fixed `/home/runner/.local/state/honua/cert-plan-stage/` paths. Its
  `bootstrapFunctionArn` names the existing cert Lambda; GetFunction is read
  only and its returned code SHA must match the committed bootstrap manifest.
- `HONUA_CERT_TFVARS_JSON`: exact protected JSON tfvars whose SHA256 is in the
  request. `HONUA_CERT_BACKEND_HCL`: protected `backend.tf` for the original S3
  backend and separate backend-access web-identity role, with the fixed private
  token-file path. No credentials or bearer token belong in that file.
- `HONUA_CERT_DEPLOY_ROLE_ARN` and `REALAWS_CERT_REGION` environment variables:
  the existing deployment role and cert region. The role must retain the exact
  `repo:honua-io/honua-server:environment:cert` trust, and must be able to read
  the existing Lambda code. The separate backend role must read/lock the
  existing state. This workflow does not broaden either role's trust.

The operator must prepare these per-dispatch protected values from the
reviewed release candidate and existing cert/backend outputs; they are not in
source. The request's `sessionExpiresAtUtc` must conservatively bound the
deployment STS session. The initialized Terraform S3 backend descriptor must
configure `assume_role_with_web_identity.duration = "1h"`. The workflow renews
the GitHub token before the producer starts. The producer checks that exact
descriptor, requires over 60 seconds of token validity at plan start, and
conservatively
reserves five minutes from the configured one-hour backend session. Terraform
itself exchanges the token for its backend STS credentials; a separate sibling
role session is not treated as proof of Terraform's credentials.

Source review and offline controls establish refusal behavior, not live AWS
permissions, original state, deployed ZIP identity or successful plan execution.
After a real protected run, the trusted receiver must authenticate the GitHub
run/attempt/artifact digest, decrypt using the dedicated local identity, verify
the complete packet, and retain it under separate custody. No approval,
exact-byte apply consumer, audit/keyring adoption or candidate qualification is
included here.
