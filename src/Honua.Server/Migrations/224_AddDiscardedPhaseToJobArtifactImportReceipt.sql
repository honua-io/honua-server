-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Allow a committed staged-import receipt to transition to 'discarded' when its
-- server-owned staging table is dropped before publication. Expand-only: widens
-- the phase check constraint without altering existing rows or columns.
ALTER TABLE $HonuaSchema$.job_artifact_import_receipt
    DROP CONSTRAINT IF EXISTS job_artifact_import_receipt_phase;

ALTER TABLE $HonuaSchema$.job_artifact_import_receipt
    ADD CONSTRAINT job_artifact_import_receipt_phase
    CHECK (phase IN ('reserved', 'committed', 'discarded'));
