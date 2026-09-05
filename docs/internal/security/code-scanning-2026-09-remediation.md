# September 2026 code-scanning remediation

The baseline API inventory captured on 2026-09-05 UTC contains 73 open alerts:
CodeQL 3, Trivy 66, Hadolint 4. This inventory differs from the original packet:
Trivy includes filesystem findings for `fflate` and `pytest`, plus Ubuntu runtime
findings and two Alpine libexpat findings. Default-branch API counts require a
new scan of the merged changes; local results are separate evidence.

## Runtime package refresh

All external base references in the root Dockerfile and Dockerfiles under
`docker/` were resolved against their registries and digest-pinned. Updated
published digests include the .NET SDK, .NET runtime-deps (glibc), Azure Functions,
Lambda provided runtime, and Java JRE. Already-current digests remain unchanged.

No newer digest was published for `dotnet/aspnet:10.0` or the Alpine SDK/runtime
bases at verification time. Their existing package upgrade steps are retained.
`RUNTIME_PACKAGE_REVISION=20260905` invalidates cached runtime package layers
for JIT and Alpine AOT so these upgrades actually run. No new blanket upgrade
step is needed for those images. The platform scan image is a local tag of the
image built from these Dockerfiles, not a separate Dockerfile.

Verified package versions include Ubuntu util-linux family `2.39.3-9ubuntu6.6`,
zlib `1:1.3.dfsg-3.1ubuntu2.2`, and Alpine libexpat `2.8.4-r0`.

The filesystem fixes update `fflate` to 0.8.3 and the remaining pystac client
`pytest` pin to 9.0.3. The rebuilt pystac image collects all 64 tests from the
actual `tests/python/stac_client` suite successfully.

## Local scan evidence

Trivy 0.70.0 matches the nightly scanner in the live alerts. Platform JIT
images also pass the exact Trivy 0.68.1 actionable SARIF configuration pinned
by the release workflow (High/Critical, fixed vulnerabilities only). The High/Critical CLI check uses the workflow's declared severity input and
existing `.trivyignore`. However, the pinned action overrides this input for
SARIF by default: `entrypoint.sh:75-79` unsets `TRIVY_SEVERITY` unless
`limit-severities-for-sarif` is true. The nightly workflow does not set that
input. Its actual SARIF scan therefore includes all severities. The earlier
High/Critical SARIF receipt is not equivalent to that action behavior.
No filter or ignore-file change is part of this remediation.

| Target | Result |
| --- | --- |
| Repository filesystem, vulnerability scanner, all severities | 0 vulnerabilities |
| Fully rebuilt JIT application image, explicit High/Critical CLI filter | 0 findings; this does not reproduce the action SARIF default |
| Fully rebuilt JIT application image, actual action-equivalent all-severity SARIF | Rerun in progress |
| Rebuilt Alpine arm64 runtime package layer, all severities | 0 findings |
| Exact JIT runtime package layer, all severities without ignores | 29 Medium, 5 Low; no vendor fixed versions |
| Fully rebuilt Lambda JIT, release actionable SARIF | 0 findings |
| Fully rebuilt Azure Functions JIT, release actionable SARIF | 0 findings |
| Rebuilt pystac image, pytest collection | 64 tests collected |

The remaining unpatched OS inventory includes glibc, libexpat, ICU, systemd,
shadow/login, tar, and wget advisories. A clean High/Critical gate does not mean
this full inventory is empty. None of these findings is dismissed or newly
suppressed by this remediation. The actual nightly SARIF includes lower severities and is being reproduced
locally. Clearing the default-branch API still requires
merged fixes and successful scans of the same analysis categories.

Full AOT and other platform-image validation is pending; the PR must record
its actual results before claiming those images are verified.

## Native-AOT startup prerequisite

The canonical image compiled in serving-image verification run 33942385536,
but its startup check rejected singleton `ZarrTileService` capturing scoped
`IMetadataV2GraphProvider`. PR #4332 changes the tile service to request scope.
Its actual registration regression fails before the change and passes after;
the startup smoke test remains enabled. Full local native image builds are
still in progress, so their final scan/startup results are not yet claimed.

## Findings newly surfaced by the fresh trunk analysis

The successful trunk CodeQL run 33942910415 closed original SQL alert #3376
and surfaced 22 additional alerts, outside the initial 73-alert inventory.
The live count immediately after that analysis was CodeQL 24, Trivy 66,
Hadolint 4. Original-packet remaining counts were CodeQL 2, Trivy 66,
Hadolint 4. New findings must not be confused with regressions introduced by
this image refresh.

The following evidence review uses trunk commit
`91db1196c9d43d1fb0a42904937c0880b5d809ce`, which the analysis actually scanned.
No query configuration or suppression is changed by this review.

| Alerts | Code path and disposition evidence |
| --- | --- |
| #3470–#3487 (`cs/sql-injection`) | Every tainted table expression in `PostgresObservationStore.cs:26-30` passes the schema to `SchemaSearchPath.QualifyTable`. That helper calls `ValidateAndQuote`, which rejects anything outside `\A[A-Za-z_][A-Za-z0-9_]{0,62}\z` and uses `NpgsqlCommandBuilder.QuoteIdentifier`. The table suffixes are the five literal `sta_*` names. Request values in the query bodies use Npgsql parameters; the tracked query-string flow is the schema identifier, which cannot introduce SQL syntax after that guard and quoting. The original packet's actual-helper/PostGIS regression suite has 13 passing tests, including injection-shaped, newline, and overlength values. This is evidence for a sanitizer-model false positive, not permission to interpolate other input. |
| #3488 (`cs/insecure-sql-connection`) | `SqlServerConnectionSecurity.RequireEncryption` constructs a `SqlConnectionStringBuilder` at line 14, then explicitly sets `Encrypt = true` in the object initializer before returning `builder.ConnectionString`. Constructing the builder opens no connection. Both `SqlServerConnectionFactory` and `SqlServerConnectionDriver` use the secured return value to open connections. The exact production helper and existing three encryption tests pass with the repository-pinned SqlClient 6.1.2: missing/false encryption becomes Mandatory, and certificate validation remains enabled when explicitly configured. |
| #3465–#3467 (`cs/user-controlled-bypass`) | The flagged `form == null` conditions in attachment add/update/delete only reject unsupported content types. Each handler first awaits `TryValidateLayerAccessAsync` with Write scope and a fixed Insert/Update/Delete operation and returns on denial. That helper always checks resource access and operation-specific data-editor authorization before returning a resource. `TryReadAttachmentFormAsync` writes HTTP 415 and returns null for unsupported content, so either branch preserves authorization. These conditions cannot bypass the earlier mandatory authorization. |

Source paths for review:

- `src/Honua.Db/Postgres/Features/SensorThings/PostgresObservationStore.cs`
- `src/Honua.Db/Postgres.Shared/Features/Infrastructure/SchemaSearchPath.cs`
- `src/Honua.Db/SqlServer/Features/Security/SqlServerConnectionSecurity.cs`
- `src/Honua.Db/SqlServer/Features/FeatureStore/Services/SqlServerConnectionFactory.cs`
- `src/Honua.Db/SqlServer/Features/Security/SqlServerConnectionDriver.cs`
- `src/Honua.Protocols.GeoServices/FeatureServer/AttachmentEndpoints.cs`

At the time this evidence was written, none of these new alerts had been
dismissed. Any subsequent false-positive disposition must include its specific
code-path evidence in the GitHub dismissal comment.
