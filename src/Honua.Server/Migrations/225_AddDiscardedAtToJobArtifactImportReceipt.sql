-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Record when a committed staged-import receipt is discarded and its server-owned staging
-- table is dropped before publication. Expand-only: adds a nullable timestamp column without
-- altering existing rows, columns, or constraints.
ALTER TABLE $HonuaSchema$.job_artifact_import_receipt
    ADD COLUMN IF NOT EXISTS discarded_at timestamptz;
