-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Records the owning tenant on analysis content items, as 120_AddStudioTenantOwnership does for
-- the Studio lifecycle tables. Analysis packages saved through the Studio family bridge are
-- authorized by Studio's tenant rail, which reads the tenant recorded on the item; without a
-- column, an item saved from one tenant was attributed to the deployment's default tenant.
--
-- tenant_id is stamped once, at creation, and never rewritten. NULL keeps meaning "the
-- deployment's MultiTenancy:DefaultTenantId" (TenantOwnership.ResolveOwnerTenant), so a
-- single-tenant upgrade keeps reading its own content.
--
-- Backfill, most precise first: an item saved through the Studio bridge has a Studio shadow row
-- (same item id, family 'analysis') that recorded the creating request's tenant, whatever kind of
-- principal owned it; otherwise a tenant-qualified owner id `subject:<iss>:<sub>@tenant:<tenant>`
-- still names the tenant.
ALTER TABLE $HonuaSchema$.analysis_content_items
    ADD COLUMN IF NOT EXISTS tenant_id TEXT;

UPDATE $HonuaSchema$.analysis_content_items AS items
SET tenant_id = studio.tenant_id
FROM $HonuaSchema$.studio_content_items AS studio
WHERE items.tenant_id IS NULL
  AND studio.family = 'analysis'
  AND studio.tenant_id IS NOT NULL
  AND items.item_id = 'analysis-content-' || replace(studio.item_id::text, '-', '');

UPDATE $HonuaSchema$.analysis_content_items
SET tenant_id = NULLIF(
        replace(substring(owner_id FROM '@tenant:(.*)$'), '%2F', '/'), '')
WHERE tenant_id IS NULL
  AND owner_id LIKE 'subject:%@tenant:%';
