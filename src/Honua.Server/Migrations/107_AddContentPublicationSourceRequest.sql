-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Exact upstream request correlation for Studio/Console publication decisions.
-- The immutable version and active route are already inserted in one transaction;
-- this unique request identity makes that transaction the authoritative decision.
ALTER TABLE IF EXISTS $HonuaSchema$.content_publication_versions
    ADD COLUMN IF NOT EXISTS source_request_id UUID NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_content_publication_versions_source_request_id
    ON $HonuaSchema$.content_publication_versions (source_request_id)
    WHERE source_request_id IS NOT NULL;
