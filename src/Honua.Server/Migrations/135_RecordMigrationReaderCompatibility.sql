-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Forward-compatibility declarations beside the DbUp journal (#6477). After each
-- successful migration run the server records, for every journaled script it ships,
-- whether the script is expand-only, the script's exact SHA-256 and LF-normalized
-- text, and the oldest reader schema it is declared safe for. A rollback image that
-- implements the check accepts a journal entry it does not ship only after its own
-- classifier re-derives an expand-only declaration from the recorded text.
--
-- Expand-only: a new table in the journal schema. Nothing existing is altered, and
-- an older reader that does not know this table never reads it.
CREATE TABLE IF NOT EXISTS public.schema_version_reader_compatibility (
    script_name VARCHAR(255) PRIMARY KEY,
    declaration_format INTEGER NOT NULL,
    classification TEXT NOT NULL,
    script_sha256 TEXT NOT NULL,
    min_reader_schema INTEGER NOT NULL,
    script_text TEXT NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
