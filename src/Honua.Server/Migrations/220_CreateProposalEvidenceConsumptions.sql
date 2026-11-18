-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Signed proposal evidence is single-use: one row per (tenant, transcript key, run nonce) records the
-- proposal that consumed it. A row may be swept once expires_at passes, because expired evidence is
-- refused before it reaches the store.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.proposal_evidence_consumptions (
    tenant_id          TEXT NOT NULL,
    transcript_key_id  TEXT NOT NULL,
    run_nonce          TEXT NOT NULL,
    proposal_id        TEXT NOT NULL,
    expires_at         TIMESTAMPTZ NOT NULL,
    consumed_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, transcript_key_id, run_nonce)
);
CREATE INDEX IF NOT EXISTS idx_proposal_evidence_consumptions_expires_at
    ON $HonuaSchema$.proposal_evidence_consumptions (expires_at);
