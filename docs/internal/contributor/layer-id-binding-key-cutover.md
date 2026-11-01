# Persisted layer binding key cutover

Migration 144 changes the Metadata v2 storage binding option from its previous key to `layerId`. It preserves each option value and every snapshot row, recalculates ETags for changed snapshots, and updates the active ETag. If a binding contains both keys, migration 144 aborts the transaction without changing data.

The migration is classified as a contract change. Existing databases must pass the configured contract migration gate after operators review the release and verify a restorable pre-cutover backup. Fresh installs have no persisted documents to rename. This database change must be released with application code that reads `layerId`.

After commit, older application versions that read the former key cannot use affected bindings. Do not roll back the application alone. Restore the verified pre-cutover database backup together with the matching application version. ETags change for affected snapshots, so clients must treat their cached representations as stale. The migration is not executed by this proposal.
