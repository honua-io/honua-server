# Curated Certification Evidence Snapshot

This directory is a small committed example snapshot anchored by ticket `#469`.

Purpose:

- Eliminate placeholder-only rows in [`docs/gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../../CLIENT_TEMPLATE_VERSION_MATRIX.md)
- Provide immutable example `.cert.json` envelopes that match the shared schema
- Keep the repo lightweight while release branches still replace these links with workflow artifact URLs or release asset URLs

Rules:

- Treat these files as curated examples, not a substitute for release-candidate evidence.
- On each release candidate, update the client version matrix to point at the current immutable workflow artifact or release asset instead of this directory.
- Keep the filenames and schema aligned with [`docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](../../CROSS_CLIENT_CERTIFICATION_EVIDENCE.md).
