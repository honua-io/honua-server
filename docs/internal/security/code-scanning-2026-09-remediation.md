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

Trivy 0.70.0 matches the scanner version in the live alerts. The application
image check uses the nightly workflow's existing High/Critical severity filter
and existing `.trivyignore`; neither the filters nor ignore files are changed.

| Target | Result |
| --- | --- |
| Repository filesystem, vulnerability scanner, all severities | 0 vulnerabilities |
| Fully rebuilt JIT application image, nightly High/Critical JSON and SARIF configuration | 0 findings |
| Rebuilt Alpine arm64 runtime package layer, all severities | 0 findings |
| Exact JIT runtime package layer, all severities without ignores | 29 Medium, 5 Low; no vendor fixed versions |
| Rebuilt pystac image, pytest collection | 64 tests collected |

The remaining unpatched OS inventory includes glibc, libexpat, ICU, systemd,
shadow/login, tar, and wget advisories. A clean High/Critical gate does not mean
this full inventory is empty. None of these findings is dismissed or newly
suppressed by this remediation. The exact nightly SARIF pass is clean; this is separate from the broader
all-severity package inventory. Clearing the default-branch API still requires
merged fixes and successful scans of the same analysis categories.

Full AOT and other platform-image validation is pending; the PR must record
its actual results before claiming those images are verified.
