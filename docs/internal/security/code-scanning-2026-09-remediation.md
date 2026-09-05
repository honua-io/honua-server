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
| Fully rebuilt JIT application image, actual action-equivalent all-severity SARIF | 34 findings: 29 Medium, 5 Low; no fixed versions listed |
| Rebuilt Alpine arm64 runtime package layer, all severities | 0 findings |
| Exact JIT runtime package layer, all severities without ignores | 29 Medium, 5 Low; no vendor fixed versions |
| Fully rebuilt Lambda JIT, release actionable SARIF | 0 findings |
| Fully rebuilt Azure Functions JIT, release actionable SARIF | 0 findings |
| Rebuilt pystac image, pytest collection | 64 tests collected |

The remaining unpatched OS inventory includes glibc, libexpat, ICU, systemd,
shadow/login, tar, and wget advisories. A clean High/Critical gate does not mean
this full inventory is empty. None of these findings is dismissed or newly
suppressed by this remediation. The reproduced actual nightly SARIF contains 34 lower-severity findings,
each with an empty fixed-version field. This prevents claiming zero open
Trivy alerts under the unchanged CI configuration. Clearing the default-branch API still requires
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

Pinned action source confirming the SARIF behavior:
[entrypoint.sh:75-79](https://github.com/aquasecurity/trivy-action/blob/ed142fd0673e97e23eac54620cfb913e5ce36c25/entrypoint.sh#L75).
The full-image action-equivalent SARIF receipt is
`/tmp/honua-security-jit-action-equivalent.sarif` on the validation host.

## Additional validation

The repository's actual `scripts/ci/pre-pr-check.sh --fast` completed
successfully: warnings-as-errors build, 68 MCP/AI tests and 286 architecture
tests passed, with no skips in either selected suite. Server integration shards
and native-AOT publishing are excluded by that script's explicit FAST mode and
are tracked separately above.

A newer supported Ubuntu candidate was also rebuilt with the exact runtime
package layer: `dotnet/aspnet:10.0-resolute` at
`sha256:e12b240891f34144edd813a11e86649dca6120165adfb5ad0a29bbde6753a975`.
Its all-severity scan had 63 findings (50 Medium, 5 Low, 8 High), so it was not
adopted. This candidate does not solve the zero-alert requirement.

Of the 34 findings in the patched Noble JIT image, nine match the original
packet: #3393, #3395, #3399, #3400, #3402, #3404, #3406, #3411 and #3412.
The other 25 are newly surfaced local Trivy findings. These local comparisons
are separate from the default-branch API counts.


## Default-branch scan receipt, 2026-09-05 06:13 UTC

Full CodeQL run [33946367080](https://github.com/honua-io/honua-server/actions/runs/33946367080)
completed successfully and closed original XML alerts #3408 and #3409.
The nightly Hadolint upload closed all four original Hadolint alerts.
The filesystem scan closed two original Trivy alerts. The refreshed nightly
JIT SARIF also surfaced the same 25 additional lower-severity Trivy findings
seen locally. Counts must now be fetched with pagination; the first 100
results alone undercount the live inventory.

| Tool | Original baseline | Live open | Original packet still open |
| --- | ---: | ---: | ---: |
| CodeQL | 3 | 22 | 0 |
| Trivy | 66 | 89 | 64 |
| Hadolint | 4 | 0 | 0 |
| Total | 73 | 111 | 64 |

CodeQL also generated a relocated XML report #3514 at
`SoapRequestXml.cs:31` claiming that ValidationType.Schema was absent.
This report was dismissed as a false positive with specific code-path and
runtime-test evidence in the API comment: lines 19-30 explicitly set Schema,
compiled server-owned SOAP schemas, ValidationFlags.None, prohibited DTDs,
and a null resolver before the call. The exact production helper and eight
committed reader regressions were rerun successfully, including malformed
SOAP rejection (0 skipped). The original PR's 20 real protocol tests include
both endpoints rejecting DTD/external-entity documents without resolution.
This disposition concerns the original XML seam, not the other 22 findings.
Those 22 remain open: automatic approval review rejected their bulk API
disposition as outside the original packet without explicit user approval.
No scanner configuration or suppression was changed.

The nightly JIT vulnerability threshold passed, but the runtime check failed:
it created an empty PostGIS database and set HONUA_SKIP_MIGRATIONS=true.
The Production schema guard correctly rejected the absent migration 031.
[PR #4339](https://github.com/honua-io/honua-server/pull/4339) removes that skip
and waits for liveness. Its exact startup and security-check shell steps
passed locally against the rebuilt full JIT image and a fresh isolated
database: migrations ran, liveness was Healthy, the root filesystem rejected
writes, and the effective user was honua. All hardening and vulnerability
checks remain enabled. Full nightly rerun is still required after landing.

The canonical native-AOT build and startup smoke passed in serving-image
verification run 33946025461 after the Zarr scope prerequisite. Full local
canonical publishing remains in progress; the two other local native builds
were stopped to relieve host memory pressure and are not claimed complete.
