-- Durable Portal group membership and item audiences (#6366). The metadata schema is
-- shared by server replicas; no process-local grant survives as an authority on Postgres.
CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.portal_community_groups (
    group_id VARCHAR(64) PRIMARY KEY,
    tenant_key TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL,
    description TEXT,
    owner_name TEXT NOT NULL,
    access_level TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    members TEXT[] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    modified_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_portal_community_groups_tenant
    ON $HonuaSchema$.portal_community_groups (tenant_key, group_id);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.portal_item_shares (
    tenant_key TEXT NOT NULL DEFAULT '',
    item_id TEXT NOT NULL,
    owner_name TEXT NOT NULL,
    group_ids TEXT[] NOT NULL DEFAULT '{}',
    PRIMARY KEY (tenant_key, item_id)
);

CREATE INDEX IF NOT EXISTS ix_portal_item_shares_groups
    ON $HonuaSchema$.portal_item_shares USING GIN (group_ids);
