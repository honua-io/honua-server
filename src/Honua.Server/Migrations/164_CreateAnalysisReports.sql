-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.analysis_reports (
    job_id TEXT COLLATE "C" NOT NULL,
    contract_version TEXT COLLATE "C" NOT NULL,
    body JSONB NOT NULL,
    stored_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (job_id, contract_version)
);

CREATE INDEX IF NOT EXISTS idx_analysis_reports_expiry
    ON $HonuaSchema$.analysis_reports (expires_at);
CREATE INDEX IF NOT EXISTS idx_analysis_reports_stored
    ON $HonuaSchema$.analysis_reports (stored_at, job_id, contract_version);
