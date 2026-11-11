# Security Policy

This repository accepts private reports for suspected security vulnerabilities in Honua Server, its published container images, and the deployment artifacts maintained in this repository.

## Reporting a Vulnerability

- Do not open public GitHub issues for suspected vulnerabilities.
- Use GitHub's private vulnerability reporting / security advisory flow for this repository when available.
- If private reporting is unavailable in your environment, contact the maintainers through a private channel first and request a secure handoff before sending exploit details.

Include:

- affected version, image tag, or commit
- deployment model (`Docker Compose`, `Helm`, `AWS`, `Azure`, and so on)
- reproduction steps or proof of concept
- impact assessment and any known mitigations
- whether the issue has already been disclosed publicly

## Scope

In scope:

- application code in `src/`
- published Honua container images
- Helm chart content maintained in [honua-io/honua-helm](https://github.com/honua-io/honua-helm)
- deployment and security guidance in `docs/guides/deploy/`

Out of scope unless Honua code is the root cause:

- customer-specific cloud account misconfiguration
- third-party managed service outages
- denial-of-service caused solely by missing edge controls that are already documented as operator responsibilities

## Response Targets

These are response targets, not a guarantee that every issue will be fixed in the same window.

| Severity | Example impact | Acknowledge | Initial triage | Target remediation guidance |
| --- | --- | --- | --- | --- |
| Critical | remote code execution, auth bypass, active secret exposure | `1` business day | `3` business days | patch or mitigation target within `7` calendar days |
| High | privilege escalation, tenant data exposure, significant integrity risk | `2` business days | `5` business days | patch or mitigation target within `30` calendar days |
| Medium | meaningful but contained security weakness with workaround | `3` business days | `10` business days | fix in the next planned release |
| Low | hardening issue or low-likelihood abuse path | `5` business days | next backlog review | reviewed at every release; fixed or recorded as not exploitable |

## Disclosure Policy

- Honua follows coordinated disclosure.
- Public disclosure should wait until a fix or documented mitigation is available.
- If a vulnerability is being actively exploited, Honua may publish mitigations before a full patch is available.
- Release notes and security advisories should identify affected versions, mitigation steps, and upgrade guidance.

## Supported Fix Path

- Security fixes are applied to the current supported release line and current development branch.
- Older releases may require customers to upgrade to receive a fix if no patch branch exists.
- A fix that customers need before the next planned release ships as a patch release with a GitHub security advisory. The maintainer procedure is [`docs/internal/security/security-patch-releases.md`](docs/internal/security/security-patch-releases.md); the customer upgrade steps are in [`docs/guides/secure/container-vulnerabilities.md`](docs/guides/secure/container-vulnerabilities.md).

## Container Image Vulnerabilities

Known vulnerabilities in the packages inside published Honua images (operating-system packages, the .NET runtime, bundled libraries) follow these fix deadlines, counted from the day the finding is confirmed:

| Severity | Fix deadline |
| --- | --- |
| Critical | patch release within `7` calendar days |
| High | patch release within `30` calendar days |
| Medium | fixed in the next planned release |
| Low | reviewed at every release |

- Every finding ends in one of two states: fixed (a base image, package or dependency update), or recorded as not affected with the reason in [`eng/security/vex-statements.json`](eng/security/vex-statements.json). A finding with no upstream fix stays open and is fixed in the first release after the fix ships.
- A not-affected decision names the exact package version, an OpenVEX justification, an impact statement and the evidence. A new package version voids it, so the finding returns for review.
- Each release publishes an OpenVEX document generated from that file, alongside its SBOM and build provenance. Every Trivy scan in CI applies the same decisions and lists the findings they suppress.
- Every supported release image is rescanned by digest every night (`.github/workflows/security-nightly.yml`, inventory in [`eng/security/supported-images.json`](eng/security/supported-images.json)). Fixable high and critical findings in a supported release are filed in a private tracker. Public issues never carry exploit detail.

## Operational Security Baseline

Honua's current operator-facing security guidance lives in:

- `docs/guides/secure/production-checklist.md`
- `docs/guides/deploy/kubernetes.md`

These documents define the shared-responsibility model for TLS termination, WAF/rate limiting, identity configuration, managed database usage, and production deployment expectations.

## Code-scanning Remediation Notes

Periodic remediation passes against the GitHub Code Scanning backlog (Trivy / CodeQL / Hadolint) are documented under `docs/internal/security/`. Each note records refreshed image digests, dismissed findings with rationale, and findings inherited from upstream base images.

- [`docs/internal/security/code-scanning-2026-Q2-remediation.md`](docs/internal/security/code-scanning-2026-Q2-remediation.md) — 2026 Q2 backlog reduction (issue [#757](https://github.com/honua-io/honua-server/issues/757))
