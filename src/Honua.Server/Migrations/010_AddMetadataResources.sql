-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Metadata resource storage (ADR-0023)

CREATE TABLE IF NOT EXISTS honua.metadata_resources (
    resource_id TEXT PRIMARY KEY,
    api_version TEXT NOT NULL,
    kind TEXT NOT NULL,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    resource_version BIGINT NOT NULL DEFAULT 1,
    generation INT NOT NULL DEFAULT 1,
    spec JSONB NOT NULL,
    status JSONB,
    labels JSONB,
    annotations JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_applied_manifest_hash TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_metadata_resources_unique
    ON honua.metadata_resources(kind, namespace, name);
CREATE INDEX IF NOT EXISTS idx_metadata_resources_kind_namespace
    ON honua.metadata_resources(kind, namespace);
CREATE INDEX IF NOT EXISTS idx_metadata_resources_name
    ON honua.metadata_resources(name);

CREATE TABLE IF NOT EXISTS honua.metadata_history (
    id BIGSERIAL PRIMARY KEY,
    resource_id TEXT NOT NULL,
    api_version TEXT NOT NULL,
    kind TEXT NOT NULL,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    resource_version BIGINT NOT NULL,
    generation INT NOT NULL,
    spec JSONB NOT NULL,
    status JSONB,
    labels JSONB,
    annotations JSONB,
    action TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_metadata_history_resource
    ON honua.metadata_history(resource_id, resource_version);
CREATE INDEX IF NOT EXISTS idx_metadata_history_kind_namespace
    ON honua.metadata_history(kind, namespace);

CREATE TABLE IF NOT EXISTS honua.metadata_compiled (
    resource_id TEXT NOT NULL,
    resource_version BIGINT NOT NULL,
    api_version TEXT NOT NULL,
    kind TEXT NOT NULL,
    artifact JSONB NOT NULL,
    compiled_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    compiler_version TEXT,
    PRIMARY KEY (resource_id, resource_version)
);

CREATE INDEX IF NOT EXISTS idx_metadata_compiled_kind
    ON honua.metadata_compiled(kind);

CREATE TABLE IF NOT EXISTS honua.metadata_indexes (
    resource_id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    resource_version BIGINT NOT NULL,
    labels JSONB,
    annotations JSONB,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_metadata_indexes_kind_namespace
    ON honua.metadata_indexes(kind, namespace);
