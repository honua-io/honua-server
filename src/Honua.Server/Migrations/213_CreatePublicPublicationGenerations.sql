-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Reserve before the first public upload. Pending generations also carry cleanup
-- obligations: a worker can die after writing bytes and before advertising them.
-- Retain origin identity and provider receipts across configuration changes and
-- worker restarts. Submission of a purge is not completion of revocation.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.public_publication_generations (
    environment TEXT NOT NULL,
    publication_id TEXT NOT NULL,
    version TEXT NOT NULL,
    service_id TEXT NOT NULL,
    layer_index INTEGER NOT NULL CHECK (layer_index >= 0),
    metadata_revision BIGINT NOT NULL CHECK (metadata_revision >= 0),
    destination_bucket TEXT NOT NULL CHECK (length(destination_bucket) > 0),
    destination_base_url TEXT NOT NULL CHECK (destination_base_url LIKE 'https://%'),
    cdn_distribution_id TEXT NOT NULL CHECK (length(btrim(cdn_distribution_id)) > 0),
    state TEXT NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending', 'ready', 'revoking', 'revoked')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    copy_completed_at TIMESTAMPTZ,
    origin_deleted_at TIMESTAMPTZ,
    purge_ids TEXT[] NOT NULL DEFAULT '{}'
        CHECK (array_position(purge_ids, NULL) IS NULL AND array_position(purge_ids, '') IS NULL),
    purge_completed_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ,
    PRIMARY KEY (environment, publication_id, version),
    CONSTRAINT public_publication_ready_requires_copy
        CHECK (state <> 'ready' OR copy_completed_at IS NOT NULL),
    CONSTRAINT public_publication_revoked_requires_completion
        CHECK (state <> 'revoked' OR
            (origin_deleted_at IS NOT NULL AND cardinality(purge_ids) > 0
             AND purge_completed_at IS NOT NULL AND revoked_at IS NOT NULL))
);
