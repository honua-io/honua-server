-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.cdn_purge_budget (
    month_utc TEXT COLLATE "C" PRIMARY KEY,
    submitted_count INTEGER NOT NULL,
    estimated_spend NUMERIC(18, 6) NOT NULL,
    exceeded_at TIMESTAMPTZ NULL,
    CONSTRAINT cdn_purge_budget_count_nonnegative CHECK (submitted_count >= 0),
    CONSTRAINT cdn_purge_budget_spend_nonnegative CHECK (estimated_spend >= 0)
);
