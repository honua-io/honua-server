---
type: reference
title: "2026.1 capacity evidence producer"
description: "Server producer status and candidate requirements for the frozen release capacity envelope."
---
# 2026.1 capacity evidence producer

The normative capacity envelope is owned by
[honua-release](https://github.com/honua-io/honua-release/blob/trunk/docs/CAPACITY-ENVELOPE-2026.1.md).
This page records server-side evidence production; it does not amend its numbers.

`capacity-soak-candidate.yml` emits one attested ZIP containing a version-2
receipt and `honua.capacity-observations/v1`. The workflow source must be the
manifest-pinned candidate, and the candidate must already contain this producer.
The [operator runbook](ops/capacity-soak-receipt.md) describes dispatch, actual
measurement definitions, failure handling and verification.

The release promise is a post-freeze, full-envelope, single-tenant local-Docker
soak with all eight GA dimensions exercised and all eight SLOs recomputed from
attested raw observations. Multi-tenancy, alerting and offline sync retain their
Preview posture. Historical aggregate-only receipts cannot satisfy that promise.

## Acceptance status — verified 2026-10-02

[Server PR #5332](https://github.com/honua-io/honua-server/pull/5332) delivered
the producer. [Issue #5314](https://github.com/honua-io/honua-server/issues/5314)
remains open for qualification:

| Acceptance criterion | Evidence and disposition |
|---|---|
| Candidate soak produces an attested ZIP accepted by the release checker | **Outstanding; released pending a new candidate containing the producer.** The currently pinned source lacks the collector, as recorded below. No qualifying run or ZIP is claimed. |
| Attestation from a different source commit is rejected | Implemented in #5332. The [independent release-verifier fixture](../tests/python/unit/test_capacity_observations.py) accepts the analytical ZIP, then changes both the certificate source digest and SLSA resolved source and requires rejection by `check_capacity_soak.py`. Certificate-shaped fixture input tests verifier behavior; it is not a cryptographic attestation. |
| Nightly soak fixed or retired | Implemented in #5332. [The legacy workflow](../.github/workflows/load-soak-nightly.yml) has no schedule and is manual diagnostics only; candidate qualification uses the approved signer. |

The [release manifest at `66a1536`](https://github.com/honua-io/honua-release/blob/66a15364fa4c725c9b25ed78ca927f0a6fe0a943/platform-manifest.yaml)
still pins server source `87966c3f7b6c840ffc4d4da0b451714ab717b18a` and image
`ghcr.io/honua-io/honua-server@sha256:069f196bfa5c7201223d4d89868934242c4ace8805a6e48c122a88d84fa6eb1a`.
That source has neither `scripts/soak/collect_capacity.py` nor
`scripts/soak/capacity_evidence.py`; its
[workflow](https://github.com/honua-io/honua-server/blob/87966c3f7b6c840ffc4d4da0b451714ab717b18a/.github/workflows/capacity-soak-candidate.yml)
uses the legacy aggregate receipt path. Running a newer producer ref with this
older candidate cannot fix the mismatch: the attested workflow source must equal
the manifest candidate. The producer landed in
[`3dc077f`](https://github.com/honua-io/honua-server/commit/3dc077f347c6f3cc47ac40b981a7f6fad1e9480c).

The [frozen lock at the same release commit](https://github.com/honua-io/honua-release/blob/66a15364fa4c725c9b25ed78ca927f0a6fe0a943/certification/capacity-envelope.v1.json)
has SHA-256 `c31ade28c813ab6523d086a31b5a7b17068e73b149146b6c434e892e1869aa53`,
identical to the [retained contract fixture](../tests/python/fixtures/capacity/README.md).
It requires at least 3,600 seconds after the receipt-contract freeze
`2026-09-29T01:15:00Z`. Passing the analytical fixture establishes implementation
compatibility with this lock, not candidate performance or qualification.

## Historical run disposition

As of this verification, the latest completed candidate soak is
[run `35126254288`, attempt 1](https://github.com/honua-io/honua-server/actions/runs/35126254288).
Its successful workflow conclusion does not qualify the current envelope. The
[published JSON receipt](https://raw.githubusercontent.com/honua-io/honua-server/7f912b18d6a6c9ff0383e102f77e74e2f891a5c7/capacity/87966c3f7b6c840ffc4d4da0b451714ab717b18a-35126254288.json)
has a valid GitHub attestation, verified again with `gh attestation verify` on
2026-10-02, but its signed facts fail the current release requirements:

| Required binding | Historical evidence and disposition |
|---|---|
| Workflow source equals the manifest candidate | The verified certificate's `sourceRepositoryDigest` and SLSA `resolvedDependencies` both name `fc278112cc4c28431457a39e65848ce9c8c8fcaa`; the receipt's candidate and observed revision name `87966c3f7b6c840ffc4d4da0b451714ab717b18a`. Reject the source mismatch even though signature verification succeeds. |
| Exact frozen lock | The receipt binds `5a19b346cc3ca767be337904c6e788d7b7e026d3746443ce66d31a1e9caebebf`, not the current `c31ade28c813ab6523d086a31b5a7b17068e73b149146b6c434e892e1869aa53`. Reject the stale lock. |
| Post-freeze observation window | Measurement began `2026-09-16T17:24:51Z`, before the current receipt-contract freeze `2026-09-29T01:15:00Z`. Its 3,600-second duration does not repair that stale window. |
| Version-2 receipt and complete raw observations in an attested ZIP | The attested subject is `capacity-soak-receipt.json`, with no `schemaVersion: 2`, `candidateIdentity`, `window` or `rawArtifacts`. Aggregate values cannot supply the missing request intervals, metrics, workload rows and recovery events. |

Keep that immutable receipt as historical evidence. Do not reuse its URL as the
qualifying ZIP, repackage it as observations, or infer qualification from its
workflow conclusion. A fresh run must meet all four bindings together.

## Qualification handoff

1. Release engineering builds and pins an immutable candidate image whose source
   contains #5332. Record the new release commit containing the matching manifest
   and lock; re-read both instead of reusing the historical pins above.
2. Dispatch the approved workflow using the [operator runbook](ops/capacity-soak-receipt.md)
   from a ref at exactly that candidate commit, with its manifest SHA, image
   digest and the resolved release commit. Collect the complete post-freeze
   window for at least the frozen minimum duration.
3. Preserve the Actions run URL and attempt, immutable published ZIP URL, ZIP
   SHA-256, verified SLSA v1 attestation, receipt and raw observations. Require the
   checker fetched from that same release commit to accept the published ZIP
   with every dimension and signal recomputed. Attach this evidence to #5314
   before closing it.

Before dispatch, record these checks against the newly resolved release commit:

| Preflight check | Required result |
|---|---|
| Producer exists in the pinned server source | The candidate contains #5332's collector, ZIP emitter and approved workflow. The existing `87966c3` pin fails this check; stop until release engineering builds and pins its replacement. |
| Dispatch source and candidate agree | The selected ref's head, workflow source, producer checkout, candidate checkout and `candidate_sha` all equal `components.honua-server.sha`. Merely checking out the candidate from a newer workflow ref is insufficient. |
| Published image and release inputs agree | `candidate_image` is the manifest's GHCR image at its immutable digest; `lock_ref` is the recorded release commit supplying the manifest, lock and checker. The served revision must subsequently match the candidate. |
| Complete published evidence can be handed off | Retain publication (`publish=true`, the default) and a steady state at least as long as the frozen minimum. A local-only `publish=false` check cannot satisfy #5314's immutable HTTPS ZIP criterion. |

The collector retains observed queue depths even when admission prevents filling
the frozen 100-job envelope. An incomplete or failing observation never becomes
a passing release receipt. Releasing the candidate-run criterion from this
pre-cut implementation work does not waive qualification, reduce the envelope,
or make a failing gate green.
