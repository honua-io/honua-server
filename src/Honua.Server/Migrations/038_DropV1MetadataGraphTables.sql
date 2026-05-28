-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- honua:compatibility-review reason=Drops v1 metadata-graph tables orphaned by
-- the Metadata v2 cutover (#1234 / ADR-0040). All six tables are
-- write-and-read-by-removed-code: the v2 cutover commit
-- (c73f491a "Metadata v2 cutover + complete v1 metadata graph removal") removed
-- the catalog subsystem, FieldType, FieldDefinition, and every repository that
-- read or wrote these tables. A grep of src/ and tests/ shows zero references
-- outside the original CREATE-TABLE migrations and tests/seed/server.yaml
-- (test-fixture bootstrap, updated in this same change). Dropping them is
-- forward-only data deletion; rollback requires restoring from backup.
--
-- File naming: this migration is numbered 038 to land cleanly above the
-- previous high-water mark (037_CreateShareExportTraffic.sql) and outside the
-- grandfathered collision range (018, 024, 025, 029, 031, 033, 034, 035)
-- documented in ADR-0045. Future migrations should continue past 038.

DROP TABLE IF EXISTS honua.manifest_pending_changes CASCADE;
DROP TABLE IF EXISTS honua.manifest_versions CASCADE;
DROP TABLE IF EXISTS honua.metadata_compiled CASCADE;
DROP TABLE IF EXISTS honua.metadata_history CASCADE;
DROP TABLE IF EXISTS honua.metadata_indexes CASCADE;
DROP TABLE IF EXISTS honua.metadata_resources CASCADE;
