-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 139_AddAdminApiKeyRestrictions.sql
-- Description: Optional per-key request restrictions for managed API keys: an origin
--              (Referer/Origin) allow-list and a client IP (CIDR) allow-list. NULL means
--              unrestricted, so every key minted before this migration keeps working as-is.
-- Dependencies: Requires admin_api_keys from 136.
-- Expand-only: two nullable columns with no default; older readers select explicit columns
-- and never read them.

ALTER TABLE $HonuaSchema$.admin_api_keys
    ADD COLUMN IF NOT EXISTS allowed_referrers TEXT[];
ALTER TABLE $HonuaSchema$.admin_api_keys
    ADD COLUMN IF NOT EXISTS allowed_ip_ranges TEXT[];

COMMENT ON COLUMN $HonuaSchema$.admin_api_keys.allowed_referrers IS
    'Origin patterns (scheme://host[:port], optional leading *.) a request Origin/Referer must match; NULL = unrestricted';
COMMENT ON COLUMN $HonuaSchema$.admin_api_keys.allowed_ip_ranges IS
    'CIDR ranges the resolved client IP must fall in; NULL = unrestricted';
