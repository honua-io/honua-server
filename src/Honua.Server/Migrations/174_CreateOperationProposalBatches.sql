-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.operation_proposal_batches (
    batch_key TEXT COLLATE "C" PRIMARY KEY,
    idempotency_key TEXT COLLATE "C" NOT NULL,
    proposal_id TEXT COLLATE "C" NOT NULL UNIQUE,
    completed BOOLEAN NOT NULL DEFAULT FALSE
);
