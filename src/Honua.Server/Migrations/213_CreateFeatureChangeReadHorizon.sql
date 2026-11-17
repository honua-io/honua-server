-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Migration: 213_CreateFeatureChangeReadHorizon.sql
-- Description: Change-feed readers take their committed horizon from this one function instead
--              of reading MAX(generation) themselves. It returns MAX(generation) today, which the
--              global feature-writer lock keeps gap-free, so behaviour is unchanged. A later release
--              that scopes the writer lock per layer replaces only this function body (for example
--              with a published committed watermark); readers of every running release then follow
--              it without a code change, which keeps a rolling upgrade from skipping a late commit.
-- Dependencies: Requires honua.feature_changes (migration 012) and the change-tracking
--              functions from migrations 067, 137 and 189.

CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
RETURNS BIGINT
LANGUAGE sql
STABLE
AS $$
    SELECT COALESCE(MAX(generation), 0)::BIGINT FROM honua.feature_changes
$$;
