# Local Helm Testing

Local Helm chart ownership and k3d/kind helper scripts now live in the separate
[`honua-helm`](https://github.com/honua-io/honua-helm) repository.

This repository no longer ships `scripts/k8s/*` wrappers or chart install
helpers.

## Use `honua-helm`

- Follow the local cluster and Helm testing guides in `honua-helm` for k3d,
  kind, chart install, upgrade, and Helm test flows.
- Build or publish the application image from `honua-server`, then point the
  `honua-helm` chart values at that image.

## What Stays Here

- Application code, Dockerfiles, and release artifacts for `Honua.Server`
- Post-deploy validation via `scripts/run-cloud-post-apply-validation.sh`
- Reusable remote post-apply validation via
  `.github/workflows/cloud-post-apply-validation.yml`

## Related Repositories

- [`honua-helm`](https://github.com/honua-io/honua-helm) for Helm charts and
  local Kubernetes workflows
- [`honua-terraform`](https://github.com/honua-io/honua-terraform) for
  Terraform infrastructure provisioning
