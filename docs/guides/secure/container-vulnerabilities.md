---
type: guide
title: "Container vulnerabilities and security updates"
description: "How Honua tracks vulnerabilities in its published container images, the fix deadlines it works to, where advisories and VEX documents are published, and how to move a deployment to a patched image."
---
# Container vulnerabilities and security updates

Honua scans every published server image for known vulnerabilities, rescans each supported release every night, and ships fixes as patch releases. This page covers what Honua does and what you do to stay current.

**Prerequisites:** A running Honua deployment and permission to update it (for the AWS install, its CloudFormation stack).

## What Honua does

- **Build-time gate.** An image with a high or critical vulnerability that has an available fix does not publish.
- **Nightly rescans by digest.** Each supported release image is rescanned every night against current advisory data, by its immutable digest. A newly disclosed advisory against a package in a shipped image is found even if nothing in Honua changed.
- **Fix deadlines.** Measured from the day a finding is confirmed:

  | Severity | Deadline |
  | --- | --- |
  | Critical | Patch release within 7 days |
  | High | Patch release within 30 days |
  | Medium | Fixed in the next planned release |
  | Low | Reviewed at every release |

  Every finding ends either fixed or recorded as not affected, with the reason. Findings with no upstream fix are fixed in the first release after the fix ships.
- **Published evidence.** Every release ships, as release assets, an SPDX SBOM, build provenance, and an [OpenVEX](https://openvex.dev) document. The OpenVEX document lists each vulnerability Honua has found does not affect the image, with the justification. Scanners that read OpenVEX (Trivy, for example) can apply it, so these findings show as documented, not hidden.

## Where to find advisories

- **Security advisories:** <https://github.com/honua-io/honua-server/security/advisories>. Watch the repository (Custom, then Security alerts) to be notified.
- **Releases, SBOMs and VEX documents:** <https://github.com/honua-io/honua-release/releases>.
- **Reporting a vulnerability:** see [SECURITY.md](https://github.com/honua-io/honua-server/blob/trunk/SECURITY.md). Do not report vulnerabilities in public issues.

## Scan the images in your own account

If you copy Honua images into your own registry, scan them there as well:

- **Amazon ECR:** turn on enhanced scanning (Amazon Inspector) for the registry, with continuous scanning for the repositories that hold Honua images. See [Enabling enhanced scanning](https://docs.aws.amazon.com/AmazonECR/latest/userguide/image-scanning-enhanced-enabling.html).
- **Runtime monitoring on ECS on Fargate:** turn on Amazon GuardDuty Runtime Monitoring with automated agent management for ECS on Fargate in each account and Region that runs Honua, then redeploy the service so new tasks pick up the agent. See [Runtime Monitoring for Amazon ECS on Fargate](https://docs.aws.amazon.com/guardduty/latest/ug/how-runtime-monitoring-works-ecs-fargate.html).

Your registry scanner may report findings that Honua's VEX document marks as not affected. Check the release's OpenVEX document for the justification before you raise a ticket.

## Move to a patched image

A patch release publishes new image digests. To upgrade the AWS (CloudFormation Launch Stack) install:

1. Read the advisory and the patch release notes. They list the new server image digest and, when the release changes it, the worker image digest.
2. In the CloudFormation console, select the Honua stack and choose **Update**, then **Use existing template**.
3. Set `ServerImageUri` to the new server image digest (`ghcr.io/honua-io/honua-server@sha256:...`, or your ECR copy of it). If heavy-job workers are enabled, set `BatchImageUri` to the worker digest from the same release. Leave every other parameter unchanged and complete the update.
4. Wait for the ECS service deployment to finish, then confirm the service is healthy: `GET /healthz/ready` returns `200`.

To roll back, update the stack again with the previous digests. If a patch release ever includes a database migration, its release notes say whether rolling back is safe.

For other installs, deploy the new image digest the same way you deploy any upgrade ([Upgrade and roll back](../deploy/upgrade-and-rollback.md)).

## Related

- [Harden a production deployment](production-checklist.md)
- [Upgrade and roll back](../deploy/upgrade-and-rollback.md)
