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
| `Dockerfile` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664` |
| `Dockerfile` | runtime | `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` | `@sha256:27b6b84beeede74fd16886177d360799c8e4299ceadfbd64eef57bafead7878a` |
| `docker/Dockerfile.aot` | build | `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | `@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c` |
| `docker/Dockerfile.aot` | runtime | `mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine` | `@sha256:ad7cd1ed2e913fbd806f8ecc0e8bb8e9e8fb7cfd4d3fa43be9aa0b4cd8008bf5` |
| `docker/Dockerfile.functions` | build + runtime | `mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0-appservice` | `@sha256:cc14ce08d684cf5a39d231484cc6c48b616f59e01d02476834bd629a259dde73` |
| `docker/Dockerfile.functions.aot` | build + runtime | `mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0-appservice` | `@sha256:cc14ce08d684cf5a39d231484cc6c48b616f59e01d02476834bd629a259dde73` |
| `docker/Dockerfile.lambda` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664` |
| `docker/Dockerfile.lambda` | runtime | `public.ecr.aws/lambda/provided:al2023` | `@sha256:6228848061d53f16eb774d4f1ddfce45c973376ad38da844dff144cc3e11e517` |
| `docker/Dockerfile.lambda(.aot)` | adapter | `public.ecr.aws/awsguru/aws-lambda-adapter:0.9.1` | `@sha256:46d6625e68cbbdd2efab4a20245977664513f13ffef47915b000d431adcea0b4` |
| `docker/Dockerfile.lambda.aot` | build | `mcr.microsoft.com/dotnet/sdk:10.0` | `@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664` |
| `docker/Dockerfile.lambda.aot` | runtime | `mcr.microsoft.com/dotnet/runtime-deps:10.0` | `@sha256:894098eafc82e5fa02ba9f2b71d426dc78252876b9e914caae77ed95cfce185a` |

The Functions images were previously the only platform-published Dockerfiles
without a manifest digest; the unpinned `:4-appservice` tag is what produced the
dominant `trivy-functions-aot-amd64` SARIF category. The pin (a) makes the
upstream contents deterministic so nightly Trivy stops re-uploading the same
advisories under shifting layers and (b) brings the Functions image in line with
the existing platform Dockerfile pattern (`Dockerfile`,
`docker/Dockerfile.aot`, `docker/Dockerfile.lambda{,.aot}`). Refresh cadence:
explicit PR per published image. Auxiliary developer/simple Dockerfiles
(`docker/Dockerfile.dev`, `docker/Dockerfile.lambda.aot.simple`) are outside
this platform publish matrix and retain their existing image references.

### Nightly mirror derives from these pins

`nightly-container-build.yml` does not pull the bases from MCR directly (MCR is
anonymous-only and rate-limits the nightly fleet); its `mirror-base-images` job
copies them into GHCR and the build jobs pass the mirrored tags as
`DOTNET_SDK_IMAGE` / `DOTNET_ASPNET_IMAGE` / `DOTNET_RUNTIME_DEPS_IMAGE`, which
override the Dockerfile ARG defaults. That workflow previously carried its own
hand-maintained digest list, so a Dockerfile-only refresh left the published
`nightly*`/`trunk*` images on the *old* bases. The digests now live only in the
Dockerfile ARG defaults: `scripts/ci/base-image-mirrors.sh` reads them and the
mirror job mirrors exactly what it prints, and `--verify` fails the run if a
build job consumes a mirror tag the map does not produce. Refreshing a pin in a
Dockerfile is therefore sufficient for the published images to pick it up.

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
| DL3008 | `docker/Dockerfile.functions{,.aot}` apt-get install | suppressed | The Functions base is digest-pinned, while the final runtime layer deliberately resolves the newest Debian security revisions available at build time. |
| DL3005 | `docker/Dockerfile.functions{,.aot}` apt-get upgrade | suppressed | The Microsoft base digest lags its Debian security repositories; applying those compatible updates removes repository-fixable runtime CVEs. |
| DL3041 | `docker/Dockerfile.lambda` dnf upgrade/install | suppressed | The Lambda base is digest-pinned, while the final runtime layer deliberately resolves the newest compatible Amazon Linux security revisions and required runtime libraries available at build time. |
| SC2086 | `Dockerfile` dotnet restore | suppressed | `EXTRA_MSBUILD_ARGS` must word-split into separate `-p:` args; quoting it produces an invalid single argument. |
| SC2086 | `Dockerfile` dotnet publish | suppressed | Same as above. |
| DL3018 | `Dockerfile` apk add | suppressed | Runtime base image is digest-pinned to a specific Alpine snapshot; apk versions are deterministic for that snapshot. |

Each suppression is inline `# hadolint ignore=...` directly above the
offending line with the rationale recorded in the surrounding comment block.

## CodeQL SQL / XML triage

| File:line | Rule | Disposition | Rationale |
| --- | --- | --- | --- |
| `src/Honua.Postgres/Features/Infrastructure/SchemaSearchPath.cs:39` | `cs/sql-injection` | dismiss | Identifier is allow-list validated against `^[A-Za-z_][A-Za-z0-9_]*$`; `SET search_path` does not accept identifier parameter binding. In-source `// codeql[cs/sql-injection]` annotation present. |
| `src/Honua.Postgres/Features/FeatureStore/Services/FeatureDataAccess.Core.cs:134` | `cs/sql-injection` | dismiss | `CreateSafeCommand` delegates to `PostgresSqlSafety.CreateReadCommand`, which calls `ValidateReadOnlySingleStatement` to reject anything but a single `SELECT` / `WITH` (no comments, no statement separator, no mutating tokens). User values bind via `NpgsqlParameter` in the surrounding callers. No per-call-site annotation — the wrapper is the dismissal anchor. |
| `src/Honua.Postgres/Features/Infrastructure/Caching/PreparedStatementCache.cs:568` | `cs/sql-injection` | dismiss | `CloneCommand` rebuilds a previously-validated command via the same `PostgresSqlSafety.CreateReadCommand` wrapper; the `CommandText` being cloned is the parameterized query the cache already owns, never raw user input. No per-call-site annotation — the wrapper is the dismissal anchor. |
| `src/Honua.DuckDB/Features/FeatureStore/Services/DuckDBFeatureDataAccess.cs:448` | `cs/sql-injection` | dismiss | `ParameterizedQuery.Sql` is built from a fixed template plus positional `?` placeholders; user values bind as parameters in the loop below. In-source `// codeql[cs/sql-injection]` annotation present. |
| `src/Honua.Server/Features/Infrastructure/Helpers/SecureXmlDocumentParser.cs:14` | `cs/xml/insecure-dtd-handling`, `cs/xml/missing-validation` | dismiss | `DtdProcessing = Prohibit`, `XmlResolver = null`, and `MaxCharactersFromEntities = 0` together block both XXE and entity-expansion attacks; this is the recommended secure XML reader configuration. In-source `// codeql[...]` annotation present. |

Three dismissals (`SchemaSearchPath.cs`, `DuckDBFeatureDataAccess.cs`,
`SecureXmlDocumentParser.cs`) carry an in-source
`// codeql[<rule-id>]: <rationale>` comment so the reasoning is reviewable in
the diff. The two Postgres call-sites that go through the
`PostgresSqlSafety.CreateReadCommand` wrapper rely on the wrapper itself
(`PostgresSqlSafety.ValidateReadOnlySingleStatement`) as the audit trail —
that helper is the central allow-list and rejects anything but a single
`SELECT` / `WITH` with no comments, statement separators, or mutating tokens.
The corresponding GitHub-UI dismissals must be filed with the same rationale
text after this PR lands so a future audit can trace each closure back to a
written justification (in-source comment for the three annotated sites,
remediation-table row + wrapper for the two Postgres sites).

## WPS 2.0 XML request validation (CodeQL `cs/xml/missing-validation`)

Alert [#3069](https://github.com/honua-io/honua-server/security/code-scanning/3069)
flagged `src/Honua.Protocols.OgcClassic/Wps20/Wps20Endpoint.cs` — the WPS XML
POST reader already prohibited DTDs, nulled the `XmlResolver`, capped entity
expansion, and bounded document size, but it never set `ValidationType`, so any
well-formed document reached the adapter's element walk unconstrained.

This one is **fixed at the root, not dismissed**. `Wps20RequestSchema` compiles a
small in-source schema (no network, no `schemaLocation` processing) that declares
exactly the five WPS 2.0 request roots the adapter dispatches — `GetCapabilities`,
`DescribeProcess`, `Execute`, `GetStatus`, `GetResult` — as envelopes whose
children and attributes are `processContents="skip"`. Nested operation content
stays under the adapter's existing bounded semantic validation
(`ValidateBindingValue`, `MaxInputs`, identifier/job-id extraction), so the schema
narrows the attack surface without duplicating protocol semantics or requiring the
full OGC schema tree to be vendored.

Supporting details:

- `XmlSchemaValidationFlags.None` keeps `ProcessSchemaLocation` and
  `ProcessInlineSchema` off, so a request cannot steer the validator at any
  remote or inline schema; the `XmlSchemaSet` and its reader both set
  `XmlResolver = null` and `DtdProcessing.Prohibit`.
- Schema failures surface as `XmlSchemaException` (which derives from
  `SystemException`, not `XmlException`, hence the separate catch clause) and are
  mapped through the same `Exception(...)` helper as every other malformed
  request. The validator's message is logged server-side only; the client sees the
  fixed `InvalidParameterValue` / "The XML request is not valid or contains
  prohibited constructs." text, so no schema, path, or parser internals leak.
- Completeness note: XSD validation alone does not reject a root element in a
  *foreign* namespace — with warnings unreported, the validator simply has no
  declaration to apply. That case is still covered, one line later, by the
  adapter's existing explicit `root.Name.NamespaceName != WpsNamespace` guard, so
  the two checks together leave no accepted root outside the five dispatched
  operations. Behaviour confirmed against the compiled schema: the five declared
  roots (including `Execute` with nested `ows:Identifier` / `wps:Input` /
  `wps:Output`) validate, an undeclared WPS-namespace root raises
  "element is not declared", a DTD is still refused before validation runs, and an
  `xsi:schemaLocation` pointing at an external host is ignored rather than fetched.
- Deliberate protocol divergence: an XML POST whose root is an *undeclared*
  element in the WPS namespace now returns `400 InvalidParameterValue` rather than
  the `501 OperationNotSupported` the KVP/GET dispatch returns for an unknown
  `request=` value. Rejecting an unparseable request document before dispatch is
  the point of the change; the KVP path is unaffected.
  `Wps20EndpointsTests.ReadRequest_XmlWithUndeclaredWpsOperation_ReturnsValidationError`
  pins that behavior and asserts the rejected element name is not echoed back.

## CodeQL user-controlled-bypass triage

| File:line | Rule | Disposition | Rationale |
| --- | --- | --- | --- |
| `src/Honua.Server/Features/Identity/Saml/SamlEndpoints.cs:83` (alert [#3059](https://github.com/honua-io/honua-server/security/code-scanning/3059)) | `cs/user-controlled-bypass` | dismiss | CodeQL reports `context.Request.HasFormContentType` (line 83) as a user-controlled condition guarding the session-creation call in `HandleAssertionConsumerService` (`AdminAuthSessionStore.CreateAuthenticatedSessionAsync`, ~line 164). The flagged condition is only a request-parsing precondition (whether to call `ReadFormAsync` at all); the actual authentication decision is `result.Succeeded` (line ~106), which comes from `SamlAssertionValidator.Validate` performing real cryptographic signature verification (`SamlSignatureVerifier.Verify`), issuer matching, and `NotBefore`/`NotOnOrAfter`/audience condition checks. An attacker cannot influence `HasFormContentType` in a way that skips assertion validation — every path to session creation still requires a signed, unexpired, correctly-issued SAML assertion. In-source `// codeql[cs/user-controlled-bypass]` annotation present. |

Same audit-follow-up pattern as the SQL/XML dismissals below: the in-source
`// codeql[cs/user-controlled-bypass]` comment above `SamlEndpoints.cs:83` is
the reviewable rationale trail, and the corresponding GitHub-UI dismissal
should reference this row.

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
  A weekly schedule and the manual `scan_only` input build local images without
  registry authentication or publishing, then upload those same categories.
  This lets a fresh scan retire stale findings even when ECR or ACR publishing
  credentials are unavailable. That step is now the *actionable* half of a
  two-pass scan — see "Actionable vs. recorded" below for the `--ignore-unfixed`
  split and the unfixed-inclusive inventory artifact that accompanies it.

Re-introduce MEDIUM severity on the Security tab by removing the new
`severity` inputs / `--severity` flag; the gating Trivy steps will continue
to fail on HIGH/CRITICAL regardless. If MEDIUM/LOW retention for ops triage
becomes a hard requirement, add a separate unfiltered Trivy step whose output
is uploaded only as an `actions/upload-artifact` artifact (not to Code
Scanning) — that is intentionally out of scope for this remediation pass. The
platform-image inventory pass added for #3036 is that shape but not that scope:
it drops `--ignore-unfixed` while keeping `--severity HIGH,CRITICAL`, so it
retains unfixed findings, not MEDIUM/LOW ones.

## Inherited findings deferred upstream

The Functions runtime layer applies all available distro package updates,
removes `libc6-dev` and `linux-libc-dev` (compile-only packages in the final
custom-handler image), and removes every non-4.x Functions extension bundle.
Honua's `host.json` requires the v4 bundle range, so the older bundles cannot be
selected at runtime.

A controlled Trivy 0.68.1 scan on 2026-07-24 measured the effect against the
then-pinned **bullseye** Functions base (`azure-functions/base:4-appservice`):

- Pinned upstream base: 784 HIGH/CRITICAL package records.
- After removing compile-only headers and unused bundles: 105 records.
- After applying available Bullseye security updates: 51 records.

Of the remaining 51 records, 45 had no fixed Debian package and six belonged to
the Microsoft-owned v4 extension bundle (`MessagePack` and `System.Text.Json`).
Those numbers are the *pre-bookworm* baseline: the base is now
`dotnet-isolated:4-dotnet-isolated9.0-appservice` (see "Moving the Functions base
off EOL Debian 11" below), which retires the bullseye won't-fix class outright.
The residual extension-bundle records cannot be upgraded independently without
replacing files owned and loaded by the Functions host; they are neither
`.trivyignore`d nor dismissed, so a future Microsoft base or extension-bundle
refresh retires them on its own.

The exact pinned Lambda base reported nine HIGH records on the same date: seven
for `glib2` and two for `libacl`. Trivy names fixed versions, but `dnf upgrade`
reported no newer packages in Amazon Linux's repository at build time. The
runtime layer now invokes `dnf upgrade` so those advisories retire as soon as
Amazon publishes the packages; until then they remain visible rather than being
ignored.

### Moving the Functions base off EOL Debian 11 (corrects an earlier conclusion)

An earlier revision of this note recorded the Debian 11 base bump requested by
[#3036](https://github.com/honua-io/honua-server/issues/3036) as **blocked
upstream**, on the basis that `azure-functions/base:4`, `:4-slim`, and
`:4-appservice` all resolve to a bullseye image and that "Microsoft publishes no
bookworm/trixie variant". The first half is true; the conclusion drawn from it
was wrong, and it is corrected here.

The mistake was searching one repository instead of the image family.
`azure-functions/base` is **abandoned**, not merely behind: its newest published
tags are host `4.636.2` (`:4`, `:4-appservice`, `:4-slim`, pushed 2025-03-06)
and host `4.637.1` (`:4-nightly*`, pushed 2025-04-22), and its highest
version-style tag, `4.1036.0.1-appservice`, is a 2024 image carrying host
`4.635.1`. Every one of them is `debian.sh --arch 'amd64' out/ 'bullseye'`.

The Functions **host** is not abandoned — Microsoft only republishes it under
the language-worker repositories, and those have moved to Debian 12. Manifests
resolved from MCR on 2026-07-31:

| Image | Base layer | `HOST_VERSION` | Published |
| --- | --- | --- | --- |
| `azure-functions/base:4-appservice` (previous pin) | bullseye | 4.636.2 | 2025-03-06 |
| `azure-functions/base:4-nightly-appservice` | bullseye | 4.637.1 | 2025-04-22 |
| `azure-functions/node:4-node20-appservice` | bullseye | 4.1051.300.6 | 2026-07-29 |
| `azure-functions/python:4-python3.12-appservice` | **bookworm** | 4.1048.200.18 | 2026-07-29 |
| `azure-functions/dotnet-isolated:4-dotnet-isolated9.0-appservice` (**new pin**) | **bookworm** | 4.1051.300.6 | 2026-07-29 |

`dotnet-isolated:4-dotnet-isolated9.0-appservice` is the pin this repo now uses.
It is the same host on a supported distro: identical
`ENTRYPOINT ["/azure-functions-host/start.sh"]`, identical
`AzureWebJobsScriptRoot=/home/site/wwwroot` and
`ASPNETCORE_CONTENTROOT=/azure-functions-host`, ~15 months newer, and it already
prunes the 2.x/3.x extension bundles upstream. Custom handlers are a *host*
feature selected by `FUNCTIONS_WORKER_RUNTIME=custom`, which
`docker/Dockerfile.functions{,.aot}` sets in the runtime layer, so the bundled
dotnet-isolated worker is never loaded — the language-worker repository is just
where Microsoft ships the maintained host today.

Runtime-dependency continuity was checked against both image histories before
the swap, because a missing native dependency here fails at *startup*, not at
build time, and CI's image build would not catch it:

| Dependency | `base:4-appservice` (old) | `dotnet-isolated:4-dotnet-isolated9.0-appservice` (new) |
| --- | --- | --- |
| ICU (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`, honua-server#1369) | `libicu67` | `libicu72` — still present |
| `libssl` | `libssl1.1` | `libssl3` |
| `libgssapi-krb5-2` | in base | **not** in base — already installed by our runtime layer |
| `libstdc++6`, `zlib1g` | in base | in base, and also installed by our runtime layer |
| .NET | host-bundled | `aspnetcore-runtime-10.0` from packages.microsoft.com |
| `libc-dev` (pulls `libc6-dev` + `linux-libc-dev`) | installed by base | still installed by base — the purge below remains load-bearing |

Two consequences of the swap are handled explicitly in the Dockerfiles:

- The new base sets `ASPNETCORE_HTTP_PORTS=8080`, which is the port `handler.sh`
  binds Honua to. `ASPNETCORE_URLS` takes precedence over `ASPNETCORE_HTTP_PORTS`
  and the base sets it to `http://+:80`, so the host still binds `:80` — but the
  runtime layer now pins `ASPNETCORE_URLS=http://+:80` itself rather than
  inheriting it, so a future base reshuffle cannot silently collide the host with
  the handler it supervises.
- The extension-bundle prune is now matched by major version
  (`! -name '4.*'`) instead of the hardcoded `2.34.0` / `3.35.0` paths, so it
  keeps holding across base refreshes that ship different bundle versions.

The `.NET runtime-deps` image floated in #3036 is still not a substitute — the
custom-handler lane needs the Functions host itself, not just a runtime.

Retained from the previous pass, and still correct: the runtime layer applies
available distro security updates, removes `libc6-dev` / `linux-libc-dev`
(compile-only in a published custom-handler image), and removes non-4.x
extension bundles. The controlled Trivy 0.68.1 measurement on 2026-07-24 against
the *old* bullseye pin was 784 HIGH/CRITICAL package records unmodified, 105
after the header and bundle removal, and 51 after applying available Bullseye
security updates.

### Actionable vs. recorded: `ignore-unfixed` on the platform-image SARIF

The previous revision declined #3036's `ignore-unfixed` request outright, on the
grounds that "an advisory with no upstream fix is still the signal that tells us
when the fix finally lands". That objection is sound and is preserved — but it
argues for *retaining* unfixed findings, not for *paging* on them, and the two
were being conflated.

`deploy-platform-images.yml` now runs Trivy twice over the same image:

1. **Actionable** — `--severity HIGH,CRITICAL --ignore-unfixed`. This is the only
   pass whose SARIF reaches GitHub Code Scanning, so the Security tab carries
   only findings this repo can close by bumping a package or a base image. This
   is not a new posture: the blocking full-CI gate in `ci.yml` ("Trivy image scan
   (full CI, HIGH/CRITICAL, fixed only)") has always used `ignore-unfixed: true`.
   The per-image SARIF was the single lane that did not, which is precisely how
   distro won't-fix records grew to ~99% of the repository's open scan volume and
   masked anything genuinely new.
2. **Inventory** — the same severities with **no** `--ignore-unfixed`, retained
   for 90 days as the `trivy-inventory-<image>-<arch>` build artifact. Nothing is
   hidden. A vulnerability whose distro fix has not shipped stays fully recorded,
   and it moves into pass 1 — and therefore onto the Security tab — the moment
   upstream publishes a fix. That is a strictly better "tell me when the fix
   lands" signal than an alert that is already open and already ignored.

Only the actionable pass honours `.trivyignore`. The inventory pass filters on
severity alone, because a risk-accepted advisory is precisely the kind of record
the inventory artifact is meant to preserve -- suppressing it there would hide it
from the complete register and keep it hidden after upstream shipped a fix. Read
the inventory artifact expecting risk-accepted advisories to be PRESENT.

`.trivyignore` stays reserved for advisories that are
unactionable for some reason *other* than "unfixed upstream", with a written
justification per entry. No CVE was added to `.trivyignore` for this change and
no alert was dismissed in the UI.

Deliberately out of scope: the gating Trivy steps in `security-nightly.yml` and
`ci.yml` are untouched (changing a gate's pass criteria is a different decision
from changing what the dashboard pages on), and `deploy.yml`'s `trivy`/`trivy-aot`
categories are untouched because those lanes scan Alpine-based images and
contributed none of the 588 alerts audited on 2026-07-30.

## Before / after alert counts

- Before (2026-04-14): 2,959 open (2,942 Trivy + 13 CodeQL + 4 Hadolint).
- Measured on `trunk` on 2026-07-30, immediately before this change lands:
  **588 open** — 587 Trivy + 1 CodeQL + 0 Hadolint. Every one of the 588 comes
  from exactly three sources:

| Source (SARIF category) | Open | Severity | Disposition |
| --- | ---: | --- | --- |
| `trivy-functions-aot-amd64` | 581 | 578 HIGH / 3 CRITICAL | **Stale signal.** Every instance was last observed between 2026-03-13 and 2026-04-13 and none has been re-observed since, because `deploy-platform-images.yml` only ran on `v*` tag pushes — a category with no new SARIF upload keeps its findings open indefinitely. The weekly schedule and `scan_only` dispatch added here re-upload the same category from `refs/heads/trunk` using the same local `honua-platform-scan:*` image name, so GitHub retires every finding the rebuilt image no longer carries. The controlled 2026-07-24 scan of the rebuilt image measured 51 remaining HIGH/CRITICAL records, down from 784 on the untouched base. |
| `.github/workflows/security-nightly.yml:container-security-scan` | 6 | 5 HIGH / 1 MEDIUM | **Fixed at the root.** All six are `Microsoft.NETCore.App.Runtime.linux-musl-x64` **10.0.8** advisories (CVE-2026-47302, CVE-2026-50524, CVE-2026-50528, CVE-2026-50651, CVE-2026-50659, CVE-2026-57108), each with fixed version 10.0.10. See "Runtime patch level" below. |
| CodeQL `cs/xml/missing-validation` (alert #3069) | 1 | MEDIUM | **Fixed at the root** — see "WPS 2.0 XML request validation" above. |

None of those 588 is cleared by an ignore-file entry, a suppression, or a UI
dismissal: the container findings clear by shipping patched bases, the CodeQL
finding by adding real validation, and the stale category by giving the scanner
a way to run again. (The dismissals recorded earlier in this note belong to the
#757 triage pass and cover different, already-closed alerts.)

### Runtime patch level

The nightly container gate scans the image built from the top-level `Dockerfile`,
which is framework-dependent and therefore inherits the .NET shared framework
from its runtime base. The previously pinned `aspnet:10.0-alpine` digest carried
`DOTNET_VERSION=10.0.8`; the digest pinned above carries
`DOTNET_VERSION=10.0.10` / `ASPNET_VERSION=10.0.10`, which is the fixed version
named by all six advisories, so the next nightly scan retires them.

The AOT and Lambda images publish self-contained, so their embedded runtime comes
from the SDK base rather than a runtime image. Both refreshed
`mcr.microsoft.com/dotnet/sdk:10.0{,-alpine}` digests ship
`DOTNET_VERSION=10.0.10` (`DOTNET_SDK_VERSION=10.0.302`), keeping every published
lane on the same patch level.

## Audit follow-up

- Each CodeQL finding **triaged as a false positive** (the SQL/XML and
  user-controlled-bypass tables above) must be **dismissed in the GitHub UI**
  with a link back to this note as the rationale source. Findings listed as
  *fixed at the root* — `cs/xml/missing-validation` #3069 and the container
  advisories — must **not** be dismissed; they close on their own once the next
  analysis or scan observes the fix, and dismissing them would hide a
  regression if the fix were later reverted.
- A future audit can re-validate any dismissal by reading the referenced
  in-source `// codeql[...]` comment plus the matching row in this table.
- The Functions image refresh cadence should ride alongside the existing
  nightly Trivy gate; track Microsoft Patch Tuesday for `azure-functions/base`
  digest bumps and propose follow-up PRs the same way every other Dockerfile
  digest is bumped today.
