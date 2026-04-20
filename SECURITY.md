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
- Helm chart content in `infrastructure/helm/`
- deployment and security guidance in `docs/devops/`

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
| Medium | meaningful but contained security weakness with workaround | `3` business days | `10` business days | fix in the next planned release or mitigation within `90` calendar days |
| Low | hardening issue or low-likelihood abuse path | `5` business days | next backlog review | fix as capacity allows |

## Disclosure Policy

- Honua follows coordinated disclosure.
- Public disclosure should wait until a fix or documented mitigation is available.
- If a vulnerability is being actively exploited, Honua may publish mitigations before a full patch is available.
- Release notes and security advisories should identify affected versions, mitigation steps, and upgrade guidance.

## Supported Fix Path

- Security fixes are applied to the current supported release line and current development branch.
- Older releases may require customers to upgrade to receive a fix if no patch branch exists.

## Operational Security Baseline

Honua's current operator-facing security guidance lives in:

- `docs/operator/security.md`
- `docs/operator/infrastructure.md`

These documents define the shared-responsibility model for TLS termination, WAF/rate limiting, identity configuration, managed database usage, and production deployment expectations.
