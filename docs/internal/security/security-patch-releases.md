# Container CVEs: rescans, VEX decisions and security patch releases

Maintainer runbook for vulnerabilities in published Honua images. The policy and fix
deadlines are in [`SECURITY.md`](../../../SECURITY.md); the customer view is
[`docs/guides/secure/container-vulnerabilities.md`](../../guides/secure/container-vulnerabilities.md).
ECS on Fargate is the deployment target this covers; Lambda images are out of scope.

## What runs automatically

| Control | Where | What it does |
| --- | --- | --- |
| Build gate | `release-bundle.yml`, `ci.yml` | Fails an image build on a HIGH/CRITICAL finding that has a fix, with the committed VEX decisions applied. (`deploy-platform-images.yml` reports to Code Scanning but does not fail.) |
| Trunk nightly | `security-nightly.yml` `container-security-scan` | Builds and scans trunk images with the committed VEX decisions applied; uploads SARIF of every severity to Code Scanning. |
| Release rescan | `security-nightly.yml` `supported-release-*` jobs | Rescans every digest in `eng/security/supported-images.json` each night with the release VEX applied, uploads SARIF (category `trivy-release-<key>`), and files fixable HIGH/CRITICAL findings. |
| Release assets | `release-security-assets.yml` (dispatch) | Produces the OpenVEX document, SPDX SBOM, Trivy verdict and attestation-free manifest reference for one release image, and the inventory change that registers its digest. |
| Dependency updates | Dependabot (NuGet, Actions, Docker) | Weekly update pull requests. |

### Where findings go

The release rescan never writes advisory ids, package names or digests into a public issue.

- **Private tracker (intended path).** Set the repository secret `SECURITY_FINDINGS_TOKEN`
  (a fine-grained token with Issues: read and write on the private security tracker only)
  and the repository variable `SECURITY_FINDINGS_REPO` (`owner/name` of that tracker). The
  report job then opens one issue per release, platform and advisory with the package, fixed
  version and due date, and skips findings that already have an open issue.
- **Fallback.** Without both values the job opens a single public issue, *Security maintenance:
  a supported release image needs a patch release*, with counts only. A maintainer files the
  detail in the private tracker by hand from the run's `release-image-security-*` artifacts.

Scan evidence itself is not confidential: the images are public, anyone can scan them, and in
this public repository the run artifacts, logs and Code Scanning SARIF are visible to any
signed-in user. What stays private is triage: exploitability analysis, mitigations and any
exploit detail, which live only in the private tracker and in draft security advisories.

The report job fails the nightly while any supported image needs a patch, so the red run is
also a signal.

## Triage a finding

1. Confirm it: the finding is in the latest nightly verdict for a supported digest (or the trunk scan).
2. Start the deadline clock from that day: critical 7 days, high 30 days, medium next release, low reviewed at every release.
3. Decide:
   - **Fix available:** bump the base image digest, the package (`RUNTIME_PACKAGE_REVISION`
     in the Dockerfiles forces the `apt-get upgrade` layer to rerun), or the NuGet/npm
     dependency on trunk. HIGH/CRITICAL in a supported release then goes through the patch
     release path below.
   - **No fix, not exploitable in Honua:** add a decision to `eng/security/vex-statements.json`
     (below). Only decisions you can show with evidence from the image belong there.
   - **No fix, possibly exploitable:** leave it open. It is fixed in the first release after
     the upstream fix ships. For HIGH/CRITICAL, record a mitigation in the private tracker.

### Record a not-affected decision (VEX)

Each entry in `eng/security/vex-statements.json` names:

- `vulnerability`: the CVE or GHSA id;
- `packages`: exact versioned purls as Trivy reports them (Debian epochs go in the `epoch=`
  qualifier, for example `pkg:deb/ubuntu/passwd@4.13%2Bdfsg1-4ubuntu3.2?epoch=1`);
- `justification`: one of the OpenVEX values (`component_not_present`,
  `vulnerable_code_not_present`, `vulnerable_code_not_in_execute_path`,
  `vulnerable_code_cannot_be_controlled_by_adversary`, `inline_mitigations_already_exist`);
- `impactStatement`: why Honua is not affected, in words a customer can check;
- `evidence`: what you inspected to establish that;
- `reviewedAt`: the review date.

`python3 scripts/security/release_images.py vex-validate` and
`scripts/security/test-release-images.py` enforce the shape. Because purls carry exact
versions, a package update voids the decision and the finding comes back for review. Never
add image findings to `.trivyignore`: ignore entries hide findings, VEX documents them. The
one remaining entry, CVE-2025-6965 (bundled SQLite in `SQLitePCLRaw.lib.e_sqlite3`, no
upstream fix, kept in step with the NuGet audit suppression), is read by the trunk JIT scan
and by the `ci.yml` image gate (Trivy's default ignore file). The release rescan, the
release-asset scan and the `release-bundle.yml` gate use an empty ignore file and report it
as an unfixed finding. Move it
to a VEX decision once its runtime reachability is established, or remove it when a fix ships.

Do not mark a shell or command-line package (`dash`, `bash`, `tar`, `coreutils`,
`util-linux`) as not in the execute path: the migration backup hook
(`PostgresDatabaseMigrationRunner`) runs an operator-configured command through `/bin/sh`.

## Release security assets

After a release image is published (release candidate or GA), for each published platform:

1. Dispatch **Release Security Assets** on `trunk` with the release id, the image by digest
   (`ghcr.io/honua-io/honua-server@sha256:...`), its platform and source revision. The run fails
   if the image has a fixable HIGH/CRITICAL finding.
2. Attach the files from the run's `release-security-assets-honua-server-<release>-<arch>`
   artifact to the release in `honua-io/honua-release`, next to the SBOM and provenance:
   `gh release upload <tag> -R honua-io/honua-release honua-server-<release>-<arch>.{openvex,spdx}.json`.
3. Register the digests in one pull request: run the `inventory-add` command from each run's
   summary, one per platform (the command is idempotent and the entries apply in sequence),
   then commit `eng/security/supported-images.json`. The run's
   `supported-images-honua-server-<release>-<arch>` artifact holds that run's patch for
   reference only: each patch is made against trunk, so do not apply both, and never copy a
   run's whole `supported-images.json` over the file. Workflows in this repository hold no
   push or merge authority, so this step is manual. From the next night the digests are rescanned.
4. When a release leaves support, remove its entries from `eng/security/supported-images.json`
   and dismiss the open alerts of its Code Scanning category (`trivy-release-<key>`), which
   stops uploading and would otherwise never close. Releases listed in `unsupportedReleases`
   (everything before 2026.1-rc.2) are refused.

### Attestation-free copies for the Marketplace registry

Release images are built with buildx provenance and SBOM attestations, so the published GHCR
digest is an image index holding the platform manifest and an `attestation-manifest` entry.
The AWS Marketplace registry rejects attestation layers. Copy the platform manifest alone;
the SBOM, VEX and provenance stay available as release assets and in GHCR.

```bash
ref="$(cat honua-server-<release>-amd64.attestation-free-ref.txt)"   # ghcr.io/honua-io/honua-server@sha256:<platform manifest>
docker buildx imagetools create --prefer-index=false --tag "<marketplace-repository>:<release>" "$ref"
docker buildx imagetools inspect --raw "<marketplace-repository>:<release>" \
  | python3 scripts/security/release_images.py assert-single-manifest
```

The copy is byte-identical to the platform manifest, so it keeps the same digest, and the
release VEX document already names that digest as a product. `assert-single-manifest` fails
if the target is an index or carries an in-toto/attestation layer. Never push the GHCR index
digest itself to that registry.

## Patch release path

Releases are cut from trunk only (`release-bundle.yml` refuses off-trunk candidates), so a
security fix is a trunk change followed by a new release.

1. **Private first.** Open a draft GitHub security advisory on `honua-io/honua-server`
   (Security, Advisories, New draft) for any vulnerability in Honua code or any HIGH/CRITICAL
   image finding customers must act on. Keep exploit detail in the draft advisory and the
   private tracker only. Request a CVE from the advisory if Honua code is the root cause.
2. **Fix on trunk.** Land the fix (base image or package bump, dependency update, or code fix)
   through the normal PR gates with a neutral title, for example
   `fix(security): move serving images to .NET 10.0.15`. For an embargoed code fix, develop
   it in the advisory's temporary private fork and merge from there.
3. **Cut the patch release.** Dispatch `release-bundle.yml` from trunk with the next release
   id (`2026.1.1`, or the next `-rc.N` before GA) and promote it on a green verdict. Publish
   it in `honua-io/honua-release` as for any release.
4. **Security assets.** Run *Release security assets* above for the new digests. In the same
   registration pull request, remove the superseded release's digests (and dismiss its
   `trivy-release-<key>` alerts): the patch release replaces it as the supported image of that line.
5. **Publish the advisory.** Set affected versions (release ids) and the patched version, link
   the release notes, and publish. The release notes list the new server (and worker) image
   digests and say whether rollback is safe.
6. **Customers upgrade** by updating their CloudFormation stack's `ServerImageUri` (and
   `BatchImageUri` when workers are enabled) to the new digests; see the customer guide.
7. Close the private tracker issue with the release id, or with the VEX decision if the
   finding was resolved as not affected.

## Triage record, 2026-11-11

Open Trivy code-scanning alerts on `trunk`: 30 (25 medium, 5 low); **no high or critical**.
The oldest is 2026-08-22. Every advisory id is triaged:

| Advisory | Package (image) | Decision |
| --- | --- | --- |
| CVE-2026-118773, CVE-2026-118764 | glob 10.5.0, minimatch 9.0.9 (managed-test-artifacts action lockfile) | Fixed: glob 14.0.2 through a scoped override (pulls minimatch 11), fast-xml-parser 5.12.0; Trivy fs reports no findings. |
| CVE-2021-31879 | wget (JIT) | Fixed earlier: wget left the runtime image in #6798. The alert belongs to a retired analysis category that no longer uploads; dismissed with that reason. |
| CVE-2025-5222 | libicu74 (AOT) | Not affected (VEX): `vulnerable_code_not_present`; genrb is not installed and no shipped ICU library has the code. |
| CVE-2024-56433 | passwd, login (AOT) | Not affected (VEX): `inline_mitigations_already_exist`; accounts are fixed at build, the image runs as UID 1001 with every setuid/setgid bit removed, and no newuidmap/newgidmap ships. |
| CVE-2026-102473, CVE-2026-102474 | dash | Open, no Ubuntu fix. `/bin/sh` is reachable through the backup hook, so not marked unaffected. |
| CVE-2026-18477, CVE-2026-18508 | tar | Open, no Ubuntu fix; the backup hook can run arbitrary operator commands. |
| CVE-2026-76642 | util-linux family | Open, no Ubuntu fix. |
| CVE-2026-40228 | libudev1 | Open (low), no Ubuntu fix. |
| CVE-2026-67693, CVE-2026-88647, CVE-2026-88648, CVE-2026-95184, CVE-2026-95210 | libgnutls30t64 | Open, no Ubuntu fix; load path not ruled out. |
| CVE-2026-107778 | libkrb5support0 | Open, no Ubuntu fix; loaded for Kerberos/GSSAPI database authentication. |
| CVE-2025-66382, CVE-2026-102633 | libexpat1 (JIT only, through fontconfig) | Open, no Ubuntu fix. |

Open items are medium or low with no upstream fix: they are fixed in the first release after
Ubuntu publishes a fix (the release rebuild runs `apt-get upgrade` on the pinned base).
Separately, the serving and worker images move from .NET 10.0.14 to 10.0.15, which fixes a
high-severity ASP.NET Core advisory.
