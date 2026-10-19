-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Lifecycle writers compare this revision in the same UPDATE that persists the transition.
-- Existing reader-only records begin at zero and remain readable.
ALTER TABLE $HonuaSchema$.promotion_deployments
    ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 0;

-- A Studio published pointer and its canonical deployment are committed together. The
-- nullable link lets old and non-hosted publication requests remain unchanged.
ALTER TABLE $HonuaSchema$.studio_publication_requests
    ADD COLUMN IF NOT EXISTS deployment_id TEXT NULL;

-- Preserve the native-vs-bridged routing decision for exact request-ID replay.
ALTER TABLE $HonuaSchema$.studio_publication_requests
    ADD COLUMN IF NOT EXISTS governs_studio_routes BOOLEAN NOT NULL DEFAULT TRUE;

-- The accepted request that last moved the item's published pointer determines its
-- current route. Request timestamps can be inverted by concurrent approvals or clocks.
-- Legacy rows remain nullable and use the preexisting timestamp fallback.
ALTER TABLE $HonuaSchema$.studio_content_items
    ADD COLUMN IF NOT EXISTS governing_publication_request_id UUID NULL;

-- A published-pointer rollback can create a successor on the accepted route.
-- Keep that successor's identity on its own immutable request receipt.
ALTER TABLE $HonuaSchema$.studio_rollback_requests
    ADD COLUMN IF NOT EXISTS deployment_id TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_rollback_requests_deployment
    ON $HonuaSchema$.studio_rollback_requests (deployment_id)
    WHERE deployment_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_publication_requests_deployment
    ON $HonuaSchema$.studio_publication_requests (deployment_id)
    WHERE deployment_id IS NOT NULL;
