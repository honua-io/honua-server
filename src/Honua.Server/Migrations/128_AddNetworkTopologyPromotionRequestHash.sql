-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Binds each topology promotion idempotency key to the request that first used it
-- (kind, target generation, expected active generation, and active row version).
-- Nullable so rows written before this column keep replaying when kind and generations match.
ALTER TABLE honua.network_topology_promotions
    ADD COLUMN IF NOT EXISTS request_hash TEXT;

COMMENT ON COLUMN honua.network_topology_promotions.request_hash IS
    'SHA-256 hex of kind|targetGeneration|expectedActiveGeneration|expectedActiveRowVersion. Null on legacy rows.';
