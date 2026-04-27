# Code-scanning remediation — 2026 Q2

This note records the remediation pass tracked by issue
[honua-io/honua-server#757](https://github.com/honua-io/honua-server/issues/757).

The Security tab on `honua-io/honua-server` reached **2,959** open code-scanning
alerts on 2026-04-14 (2,942 Trivy + 13 CodeQL + 4 Hadolint), dominated by
container/package-scan noise from the unpinned Azure Functions image. This pass
combines a base-image pin, six logging redactions, an OGC Processes redirect
hardening, Hadolint cleanup, CodeQL SQL / XML triage, and a SARIF upload filter
so the dashboard only carries actionable findings going forward.

## Refreshed image digests

| Dockerfile | Stage | Image | Digest |
| --- | --- | --- | --- |
| `Dockerfile` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:8a90a473da5205a16979de99d2fc20975e922c68304f5c79d564e666dc3982fc` |
| `Dockerfile` | runtime | `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` | `@sha256:60eb031b554df75a4b9f358290a2fa15d8961a3bc79b47bb34a00e31f7b78c69` |
| `docker/Dockerfile.aot` | build | `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | `@sha256:0191ff386e93923edf795d363ea0ae0669ce467ada4010b370644b670fa495c1` |
| `docker/Dockerfile.aot` | runtime | `mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine` | `@sha256:4f08c162590324de60f31937f5b5fa9f2b5eddaa4f0aaec3c872f855bf16c36c` |
| `docker/Dockerfile.functions` | build + runtime | `mcr.microsoft.com/azure-functions/base:4-appservice` | `@sha256:e15f9ae39a777d8ff1da4f1e74e2847f21cff6e44b10867fd0b9ee2eb23ebdb9` |
| `docker/Dockerfile.functions.aot` | build + runtime | `mcr.microsoft.com/azure-functions/base:4-appservice` | `@sha256:e15f9ae39a777d8ff1da4f1e74e2847f21cff6e44b10867fd0b9ee2eb23ebdb9` |
| `docker/Dockerfile.lambda` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:8a90a473da5205a16979de99d2fc20975e922c68304f5c79d564e666dc3982fc` |
| `docker/Dockerfile.lambda` | runtime | `public.ecr.aws/lambda/provided:al2023` | `@sha256:d7677a5f8e4468b52f46e9246b54878d1317644cc3c0182f9cb2e35180fc50c9` |
| `docker/Dockerfile.lambda(.aot)` | adapter | `public.ecr.aws/awsguru/aws-lambda-adapter:0.9.1` | `@sha256:46d6625e68cbbdd2efab4a20245977664513f13ffef47915b000d431adcea0b4` |
| `docker/Dockerfile.lambda.aot` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:8a90a473da5205a16979de99d2fc20975e922c68304f5c79d564e666dc3982fc` |
| `docker/Dockerfile.lambda.aot` | runtime | `mcr.microsoft.com/dotnet/runtime-deps:10.0` | `@sha256:962ef681468320cc5ef25fa18259cf3200247cec2ee96c2574174d4824272151` |

The Functions images were previously the only Dockerfiles in the repo without a
manifest digest; the unpinned `:4-appservice` tag is what produced the dominant
`trivy-functions-aot-amd64` SARIF category. The pin (a) makes the upstream
contents deterministic so nightly Trivy stops re-uploading the same advisories
under shifting layers and (b) brings the Functions image in line with the
existing Dockerfile pattern (`Dockerfile`, `docker/Dockerfile.aot`,
`docker/Dockerfile.lambda{,.aot}`). Refresh cadence: explicit PR per image,
matching every other Dockerfile in the repo.

## Logging redactions (CodeQL)

A new helper, `Honua.Core.Features.Infrastructure.Logging.LogValueRedactor`,
exposes `Hash` (8-char SHA-256 prefix) and `SanitizeForLog` (CR/LF stripping
with bounded length). The helper is AOT-safe (no reflection,
`SHA256.HashData`, span-based copy) and is used at the six call sites flagged by
CodeQL:

| File | Change |
| --- | --- |
| `src/Honua.Server/Features/Infrastructure/Authentication/AdminAuthSessionStore.cs` | Replaces three `LogWarning(... key)` calls with source-generated messages that emit a fixed key family (`admin-auth:pending` / `admin-auth:session`) plus an 8-char correlation hash of the session id. |
| `src/Honua.Server/Features/Infrastructure/RateLimiting/RateLimitingMiddleware.cs` | Splits the rate-limit key into a fixed family (`api_key` / `ip`) and a hashed suffix. Bearer tokens and IPs no longer enter log output. |
| `src/Honua.Server/Features/Infrastructure/Caching/RedisCacheService.cs` | Two ad-hoc `LogWarning` index-tracking calls converted to source-generated messages on the existing `RedisCacheServiceLog` partial; all cache-key parameters now emit `{KeyFamily} {KeyHash}` instead of the raw prefixed key. |
| `src/Honua.Core/Features/Infrastructure/Monitoring/DefaultPerformanceMonitor.cs` | `RecordErrorWithContext` sanitizes each scope value through `LogValueRedactor.SanitizeForLog` before the `BeginScope`, so user-controlled values cannot smuggle CR/LF or unbounded text into structured logs. |
| `src/Honua.Server/Features/Infrastructure/Rendering/MonitoredCoordinateTransformer.cs` | Raw `extent`, `point`, lon/lat, and x/y values dropped from the error context and replaced with stable `*_hash` correlation tokens. SRIDs (`from_srid`/`to_srid`) preserved for geodesy diagnostics. |
| `src/Honua.Server/Features/Infrastructure/Middleware/TestSchemaMiddleware.cs` | Invalid header values are now logged as a SHA-256 prefix; the response error detail no longer echoes the rejected header. |

Audit-trail invariant: every fix replaces the *value*, not the log line — every
event id and call site is preserved, so existing operational queries continue to
match. Cross-event correlation (multi-event for the same session id / API key)
still works via the stable hash prefix; tying a hash back to a specific user
requires an external lookup, which is the intended tradeoff.

## OGC Processes `Location` (CodeQL open-redirect)

`src/Honua.Server/Features/Protocols/Ogc/Api/Processes/ProcessEndpoints.cs`
builds the `Location` header from `BaseUrlResolver.GetBaseUrl(...)`, which
returns the configured `Public:BaseUrl` / `PUBLIC_BASE_URL` value when set and
otherwise derives a safe origin from the connection's local endpoint (or the
request `PathBase`). The resolver never reads the request `Host` header, so
the `Location` target cannot be steered by an attacker-controlled host even
when no public base URL is configured. Production deployments that need
absolute URLs configure the public base URL exactly as they would for any
other link generator.

## Hadolint dispositions

| Code | Line | Disposition | Rationale |
| --- | --- | --- | --- |
| DL3008 | `Dockerfile` apt-get install | suppressed | SDK base image is digest-pinned; pinning apt versions here forces a parallel update on every digest bump. |
| SC2086 | `Dockerfile` dotnet restore | suppressed | `EXTRA_MSBUILD_ARGS` must word-split into separate `-p:` args; quoting it produces an invalid single argument. |
| SC2086 | `Dockerfile` dotnet publish | suppressed | Same as above. |
| DL3018 | `Dockerfile` apk add | suppressed | Runtime base image is digest-pinned to a specific Alpine snapshot; apk versions are deterministic for that snapshot. |

Each suppression is inline `# hadolint ignore=...` directly above the
offending line with the rationale recorded in the surrounding comment block.

## CodeQL SQL / XML triage

| File:line | Rule | Disposition | Rationale |
| --- | --- | --- | --- |
| `src/Honua.Postgres/Features/Infrastructure/SchemaSearchPath.cs:39` | `cs/sql-injection` | dismiss | Identifier is allow-list validated against `^[A-Za-z_][A-Za-z0-9_]*$`; `SET search_path` does not accept identifier parameter binding. |
| `src/Honua.Postgres/Features/FeatureStore/Services/FeatureDataAccess.Core.cs` | `cs/sql-injection` | dismiss | All commands are built by vetted query builders that bind user-controlled values via `NpgsqlParameter`; the existing `// codeql[cs/sql-injection]` annotation in `CreateSafeCommand` covers the wrapper. |
| `src/Honua.Postgres/Features/Infrastructure/Caching/PreparedStatementCache.cs:840` | `cs/sql-injection` | dismiss | The wrapped `CommandText` is the previously prepared, parameterized query owned by this cache; never raw user input. EXPLAIN cannot accept the inner statement via parameter binding. |
| `src/Honua.DuckDB/Features/FeatureStore/Services/DuckDBFeatureDataAccess.cs:432` | `cs/sql-injection` | dismiss | `ParameterizedQuery.Sql` is built from a fixed template plus positional `?` placeholders; user values bind as parameters in the loop below. |
| `src/Honua.Server/Features/Infrastructure/Helpers/SecureXmlDocumentParser.cs` | `cs/xml/insecure-dtd-handling` | dismiss | `DtdProcessing = Prohibit`, `XmlResolver = null`, and `MaxCharactersFromEntities = 0` together block both XXE and entity-expansion attacks; this is the recommended secure XML reader configuration. |

Each dismissal carries an in-source `// codeql[<rule-id>]: <rationale>` comment
so the reasoning is reviewable in the diff. The corresponding GitHub-UI
dismissals must be filed with the same rationale text after this PR lands so a
future audit can trace each closure back to a written justification.

## SARIF upload changes

- `.github/workflows/security-nightly.yml` — both Trivy SARIF generation steps
  (filesystem and container) now set `severity: HIGH,CRITICAL`. The same
  filtered SARIF is what gets uploaded both to GitHub Code Scanning and to the
  workflow's `actions/upload-artifact` artifact, so lower severities are
  **intentionally not retained** by this workflow — Trivy is not invoked a
  second time at MEDIUM/LOW. The existing fail-on-HIGH/CRITICAL gates are
  unchanged. (The previous `container-security.yml` and `trivy-nightly.yml`
  workflows were consolidated into `security-nightly.yml` on `trunk` as part
  of PR #805 — this filter lives in their successor.)
- `.github/workflows/deploy-platform-images.yml` — the per-image Trivy CLI
  invocation adds `--severity HIGH,CRITICAL`. The single per-image SARIF that
  gets uploaded to GitHub Code Scanning is produced by this filtered run, so
  here too lower severities are not retained. SARIF categories per matrix
  entry are unchanged so the dashboard still attributes findings per image.

Re-introduce MEDIUM severity on the Security tab by removing the new
`severity` inputs / `--severity` flag; the gating Trivy steps will continue
to fail on HIGH/CRITICAL regardless. If MEDIUM/LOW retention for ops triage
becomes a hard requirement, add a separate unfiltered Trivy step whose output
is uploaded only as an `actions/upload-artifact` artifact (not to Code
Scanning) — that is intentionally out of scope for this remediation pass.

## Inherited findings deferred upstream

The Functions and Lambda base images still ship with package advisories that
originate from Microsoft's and Amazon's distribution channels and cannot be
patched in this repository. These will be picked up by the digest pin's normal
refresh cadence (a new image release retires the alert). They are intentionally
left as visible HIGH/CRITICAL signal in the Security tab so the upstream cadence
is observable.

## Before / after alert counts

Recorded in the PR description after the Security tab refresh:

- Before (2026-04-14): 2,959 (2,942 Trivy + 13 CodeQL + 4 Hadolint)
- After: to be filled in once the Trivy nightly + SARIF filter run on `trunk`
  has settled. The expected drop is at least the `trivy-functions-aot-amd64`
  category, which is the dominant single contributor.

## Audit follow-up

- Each CodeQL finding must be **dismissed in the GitHub UI** with a link back
  to this note as the rationale source.
- A future audit can re-validate any dismissal by reading the referenced
  in-source `// codeql[...]` comment plus the matching row in this table.
- The Functions image refresh cadence should ride alongside the existing
  nightly Trivy gate; track Microsoft Patch Tuesday for `azure-functions/base`
  digest bumps and propose follow-up PRs the same way every other Dockerfile
  digest is bumped today.
