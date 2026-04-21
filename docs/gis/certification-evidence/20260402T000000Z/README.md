# Historical Certification Evidence Snapshot

This directory is a small committed historical snapshot anchored by ticket `#469`.

Purpose:

- Eliminate placeholder-only rows in [`docs/gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../../CLIENT_TEMPLATE_VERSION_MATRIX.md)
- Provide immutable example `.cert.json` envelopes that match the shared schema
- Keep the repo lightweight while newer release evidence is published as workflow artifacts or release assets

Rules:

- Treat these files as curated historical examples, not a substitute for current release evidence.
- Update the client version matrix when a newer immutable workflow artifact or release asset is available.
- Keep the filenames and schema aligned with [`docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](../../CROSS_CLIENT_CERTIFICATION_EVIDENCE.md).
