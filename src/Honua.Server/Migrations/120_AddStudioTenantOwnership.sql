-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Records the owning tenant on the Studio lifecycle tables (honua-server#4905).
--
-- The Studio lifecycle stores every tenant's drafts, immutable versions and content items in
-- one set of tables. Without a tenant column the endpoints could only scope by owner id, and
-- the admin role bypasses ownership -- so a tenant-scoped administrator of one tenant could
-- enumerate, read and propose publication for another tenant's content in the default
-- (single-schema) multi-tenancy posture.
--
-- tenant_id follows owner_id exactly: it is stamped once, at creation, from the tenant the
-- creating request resolved to (PostgresStudioPackageStore.UpsertItemAsync's ON CONFLICT clause
-- deliberately excludes it, so a later draft/version upsert can never move an item between
-- tenants) and is read back on every lifecycle path.
--
-- Backfill: rows created before this migration record no tenant. At runtime a NULL tenant_id is
-- attributed to the deployment's configured MultiTenancy:DefaultTenantId (see
-- TenantOwnership.ResolveOwnerTenant), which is exactly the tenant every request in a
-- single-tenant deployment resolves to -- so an upgrade keeps reading its own content while a
-- tenant-tagged principal never inherits it. Rows whose owner id already carries the
-- issuer-and-tenant-qualified form `subject:<iss>:<sub>@tenant:<tenant>` (honua-server#3429) do
-- know their tenant, so those are backfilled precisely rather than collapsing onto the default.
--
-- Sequence numbering per ADR-0045: `119` is the current highest prefix as of this migration.
ALTER TABLE $HonuaSchema$.studio_content_items
    ADD COLUMN IF NOT EXISTS tenant_id TEXT;

ALTER TABLE $HonuaSchema$.studio_package_drafts
    ADD COLUMN IF NOT EXISTS tenant_id TEXT;

ALTER TABLE $HonuaSchema$.studio_content_versions
    ADD COLUMN IF NOT EXISTS tenant_id TEXT;

-- Recover the tenant that is already encoded in a tenant-qualified owner id. The suffix is
-- percent-encoded by Uri.EscapeDataString, so it is decoded the same way here; an empty tenant
-- segment means the owner key was minted without a resolved tenant and stays NULL.
UPDATE $HonuaSchema$.studio_content_items
SET tenant_id = NULLIF(
        replace(substring(owner_id FROM '@tenant:(.*)$'), '%2F', '/'), '')
WHERE tenant_id IS NULL
  AND owner_id LIKE 'subject:%@tenant:%';

UPDATE $HonuaSchema$.studio_package_drafts
SET tenant_id = NULLIF(
        replace(substring(owner_id FROM '@tenant:(.*)$'), '%2F', '/'), '')
WHERE tenant_id IS NULL
  AND owner_id LIKE 'subject:%@tenant:%';

UPDATE $HonuaSchema$.studio_content_versions
SET tenant_id = NULLIF(
        replace(substring(owner_id FROM '@tenant:(.*)$'), '%2F', '/'), '')
WHERE tenant_id IS NULL
  AND owner_id LIKE 'subject:%@tenant:%';

-- Enumeration is always tenant-scoped first, then ordered by the list cursor's (updated_at,
-- id) key, so the tenant column leads both listing indexes.
CREATE INDEX IF NOT EXISTS idx_studio_content_items_tenant_list
    ON $HonuaSchema$.studio_content_items (tenant_id, updated_at DESC, item_id DESC);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_tenant_list
    ON $HonuaSchema$.studio_package_drafts (tenant_id, updated_at DESC, draft_id DESC);
