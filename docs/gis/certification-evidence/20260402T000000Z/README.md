# Historical Certification Evidence Snapshot

This directory is a small committed historical snapshot anchored by ticket `#469`.

Purpose:

- Preserve historical, non-certifying schema examples for [`docs/gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../../CLIENT_TEMPLATE_VERSION_MATRIX.md)
- Demonstrate the legacy envelope shape without entering certification collectors
- Keep the repo lightweight while newer release evidence is published as workflow artifacts or release assets

Rules:

- Treat these files as curated historical examples, not certification evidence or a substitute for current release evidence.
- Update the client version matrix when a newer immutable workflow artifact or release asset is available.
- Keep the `.cert.example.json` suffix so `*.cert.json` evidence collectors cannot ingest these files.
