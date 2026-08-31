# Serving-image content-addressed verification skip

`Serving Image Boundary` can reuse successful verification evidence when a
selected serving variant has the exact same image-input content address as an
earlier successful run. The optimization is disabled unless the repository
variable `HONUA_SERVING_IMAGE_SKIP` is exactly `true`.

The content address is the v3 digest introduced by #3331 and produced by
`scripts/ci/native-image-impact.py`: a SHA-256 over the selected Git tree paths,
file modes, and blob IDs for `serving_generic`, `serving_lambda`, or
`serving_functions`. It therefore consumes the same selector contract built in
#3227 and moved into trusted default-branch observation by #3237. Branch names,
commit proximity, Buildx cache hits, and path-only comparisons are not reuse
evidence.

## Decision contract

Every selected matrix leg performs an exact-key marker lookup. A marker is
written only after that variant's build and boundary verification succeed; the
generic marker is later still, after its HTTP liveness probe succeeds. The
marker repeats its schema, variant, and full digest and is validated before a
hit can skip work. Missing, malformed, mismatched, or ambiguous evidence always
falls back to the complete build and verification path. No prefix or restore-key
fallback exists.

Each matrix leg writes its full digest, decision, and reason to the Actions run
summary. The decisions are `reuse-skip`, `build-and-verify`, or `not-selected`.
This makes both optimization and rollback visible; a skipped build is never
represented as fresh verification.

The repository variable controls consumption only. Successful runs continue to
mint markers while consumption is off, so the default behavior remains a full
verification and promotion does not begin with a cold evidence store.

## Promotion and rollback

Keep `HONUA_SERVING_IMAGE_SKIP` absent or `false` until all of these are true:

1. at least 30 selected variant legs have completed with the feature off;
2. at least three exact-address repeats exist across those legs;
3. replay shows every proposed hit has the same variant and 64-hex v3 digest as
   an earlier successful authoritative leg;
4. zero missing/malformed-marker cases produce a skip in fixtures or retained
   run summaries;
5. the Impact Routing Evidence Ledger is green for seven consecutive days with
   receipt loss below its 5% budget; and
6. a reviewer confirms PR Gate path filters, fork handling, permissions,
   publication lanes, and nightly/release/deploy re-verification are unchanged.

Promotion is the single operator action:

```bash
gh variable set HONUA_SERVING_IMAGE_SKIP --body true
```

Monitor the first 30 selected legs. Roll back immediately on any unexplained
skip, marker validation error, digest disagreement, or authoritative image
failure:

```bash
gh variable set HONUA_SERVING_IMAGE_SKIP --body false
```

Rollback affects the next job evaluation and requires no code revert. It does
not delete markers; with consumption off, every selected leg rebuilds and
re-verifies normally.
