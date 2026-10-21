# MCP grpc-web startup regression at 0f663b0

This historical packet is superseded by the authentication fix already on trunk.
No additional runtime or workflow change is needed for the diagnosed failure.

## Diagnosis

The [diagnosis comment](https://github.com/honua-io/honua-server/commit/0f663b066900658f035c07e303f47116d99613c5#commitcomment-205852190)
identifies [PR #5921](https://github.com/honua-io/honua-server/pull/5921),
`36fa28bab37e64b68b91ad4de1e6df77a8c77d39`, as the first bad landing.
The artifact inventory for run `38222670673` contains five artifacts and no
`mcp-certification-grpc-web` receipt or TRX. Historical job `114754472577`
failed setup; certification, artifact generation and identity verification were
skipped. The subsequent missing-artifact upload failure is secondary.

The landing activates the SDK certification scripts and exposes job-level
`HONUA_DEV_AUTH=true` to the shared Development server. The existing configuration
validator deliberately rejects that flag outside Test. The previous successful
jobs at `44c56748` and `0e03bdd06b` skipped certification because their SDK source
had no certification scripts. Those green statuses are not live certification.
The red persists at `0f663b0`; its GP dispatch change did not cause this failure.

## Existing repair

[PR #5938](https://github.com/honua-io/honua-server/pull/5938),
`5873a38cbe6e5362f1a5670da4e6ad3e30b946dc`, removes the legacy flag from
certification and smoke clients, retains the real admin API-key credential, and
adds the environment regression fixture to CI router validation. It is an
ancestor of this packet's starting trunk, `3eddae25f2`.

No revert is appropriate for this superseded packet: the repair is already
landed, and reverting the original landing would remove active certification,
seed corrections and receipt validation. This record preserves those changes.
No MCP production code or .NET test changes are required.

## Verification scope

The existing `scripts/ci/fixtures/validate-mcp-server-auth.py` passes all seven
checks, including rejection of inherited job/workflow bypasses, missing or
mismatched credentials and a substituted Test environment. The four tests in
`scripts.certification.test_verify_mcp_live_certification` pass.

These checks establish the startup configuration repair and receipt validation;
they do not establish a new live grpc-web certification result. This packet
neither dispatches CI nor claims new live certification evidence. The full
trailing matrix remains the qualification authority.
