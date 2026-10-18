-- Ownership is server-authored provenance, never inferred from legacy display names or metadata.
ALTER TABLE $HonuaSchema$.workflow_packages
    ADD COLUMN IF NOT EXISTS owner_actor_id text,
    ADD COLUMN IF NOT EXISTS owner_tenant_id text;
ALTER TABLE $HonuaSchema$.workflow_package_versions
    ADD COLUMN IF NOT EXISTS owner_actor_id text,
    ADD COLUMN IF NOT EXISTS owner_tenant_id text;
