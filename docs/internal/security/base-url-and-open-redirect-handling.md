# Base URL resolution and open-redirect handling

How Honua derives absolute URLs in response bodies and headers, and why the
result cannot be steered by an attacker-controlled `Host` header.

## Resolution rules

`BaseUrlResolver.GetBaseUrl(...)` (in `Honua.Server.Features.Infrastructure`)
produces the base URL used by HATEOAS link generators and the OGC API
Processes job-submission `Location` header. The resolution order is:

1. The configured `Public:BaseUrl` (env: `PUBLIC_BASE_URL`), if set.
2. The request's `PathBase` joined to a safe origin derived from the
   connection's local endpoint (its IP and port).

The resolver **never reads the request `Host` header**, so a malicious
upstream cannot steer a relative redirect or absolute link by forging that
header.

## Operator guidance

Production deployments behind a proxy should set `PUBLIC_BASE_URL` to the
externally-visible origin (scheme + host + optional port). When the variable
is unset, link generation still works — the connection's local endpoint is a
safe fallback — but emitted absolute URLs will reflect the internal address
the proxy connects to, which usually isn't what callers expect.

This is the same setting used by every link generator in the server; there
is no separate configuration for redirect-style responses.

## Affected response paths

- OGC API Processes job submission (`POST /ogc/processes/processes/{processId}/execution`) — the `201 Created` response's `Location` header.
- All HATEOAS link bodies (OGC API Features, Tiles, Records, etc.).

## Reviewer notes

The CodeQL `cs/url-redirection-from-host` rule used to flag the OGC Processes
endpoint before `BaseUrlResolver` was introduced. The dismissal of that
finding (and the matching rationale) is recorded in the relevant code-scanning
remediation note under `docs/security/`.
