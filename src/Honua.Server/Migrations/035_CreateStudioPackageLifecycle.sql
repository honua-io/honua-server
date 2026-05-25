-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Studio package lifecycle persistence for Console authoring (#1180).
-- Draft rows are mutable; content-version rows are append-only and never updated
-- after insertion. Pointer transitions are stored on studio_content_items and
-- audited through publication/rollback request tables.

CREATE TABLE IF NOT EXISTS honua.studio_content_items (
    item_id              UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    package_key          TEXT        NOT NULL,
    workspace_id         TEXT        NULL,
    family               TEXT        NOT NULL,
    current_version_id   UUID        NULL,
    published_version_id UUID        NULL,
    created_by           TEXT        NULL,
    updated_by           TEXT        NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_content_items_family
        CHECK (family IN ('query','analysis','map','dashboard','report','form','app','workflow','gp','etl'))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_content_items_workspace_key
    ON honua.studio_content_items (
        (COALESCE(NULLIF(BTRIM(workspace_id), ''), '')),
        family,
        package_key
    );

CREATE INDEX IF NOT EXISTS idx_studio_content_items_updated
    ON honua.studio_content_items (updated_at DESC);

CREATE TABLE IF NOT EXISTS honua.studio_package_drafts (
    draft_id        UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id         UUID        NOT NULL REFERENCES honua.studio_content_items(item_id) ON DELETE CASCADE,
    package_key     TEXT        NOT NULL,
    workspace_id    TEXT        NULL,
    owner_id        TEXT        NULL,
    family          TEXT        NOT NULL,
    envelope        JSONB       NOT NULL,
    validation      JSONB       NOT NULL,
    base_version_id UUID        NULL,
    generation      BIGINT      NOT NULL DEFAULT 1,
    created_by      TEXT        NULL,
    updated_by      TEXT        NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_package_drafts_family
        CHECK (family IN ('query','analysis','map','dashboard','report','form','app','workflow','gp','etl'))
);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_item
    ON honua.studio_package_drafts (item_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_owner
    ON honua.studio_package_drafts (owner_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS honua.studio_content_versions (
    version_id      UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id         UUID        NOT NULL REFERENCES honua.studio_content_items(item_id) ON DELETE CASCADE,
    package_key     TEXT        NOT NULL,
    workspace_id    TEXT        NULL,
    owner_id        TEXT        NULL,
    version_number  INTEGER     NOT NULL,
    content_hash    TEXT        NOT NULL,
    envelope        JSONB       NOT NULL,
    validation      JSONB       NOT NULL,
    dependencies    JSONB       NOT NULL,
    provenance      JSONB       NOT NULL,
    source_draft_id UUID        NULL,
    base_version_id UUID        NULL,
    change_note     TEXT        NULL,
    created_by      TEXT        NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_content_versions_version_number CHECK (version_number > 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_content_versions_item_number
    ON honua.studio_content_versions (item_id, version_number);

CREATE INDEX IF NOT EXISTS idx_studio_content_versions_item_hash
    ON honua.studio_content_versions (item_id, content_hash);

CREATE INDEX IF NOT EXISTS idx_studio_content_versions_item_created
    ON honua.studio_content_versions (item_id, created_at DESC);

CREATE TABLE IF NOT EXISTS honua.studio_content_version_dependencies (
    version_id            UUID    NOT NULL REFERENCES honua.studio_content_versions(version_id) ON DELETE CASCADE,
    item_id               UUID    NOT NULL REFERENCES honua.studio_content_items(item_id) ON DELETE CASCADE,
    dependency_kind       TEXT    NOT NULL,
    dependency_ref        TEXT    NOT NULL,
    dependency_version_id TEXT    NULL,
    required              BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_content_version_dependencies_unique
    ON honua.studio_content_version_dependencies (
        version_id,
        dependency_kind,
        dependency_ref,
        (COALESCE(dependency_version_id, ''))
    );

CREATE INDEX IF NOT EXISTS idx_studio_content_version_dependencies_ref
    ON honua.studio_content_version_dependencies (dependency_kind, dependency_ref);

CREATE TABLE IF NOT EXISTS honua.studio_publication_requests (
    request_id              UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id                 UUID        NOT NULL REFERENCES honua.studio_content_items(item_id) ON DELETE CASCADE,
    version_id              UUID        NOT NULL REFERENCES honua.studio_content_versions(version_id) ON DELETE CASCADE,
    intent                  JSONB       NULL,
    status                  TEXT        NOT NULL,
    validation              JSONB       NOT NULL,
    warning_acknowledgement TEXT        NULL,
    requested_by            TEXT        NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_publication_requests_status
        CHECK (status IN ('accepted','pending','rejected'))
);

CREATE INDEX IF NOT EXISTS idx_studio_publication_requests_item_created
    ON honua.studio_publication_requests (item_id, created_at DESC);

CREATE TABLE IF NOT EXISTS honua.studio_rollback_requests (
    request_id        UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id           UUID        NOT NULL REFERENCES honua.studio_content_items(item_id) ON DELETE CASCADE,
    target_version_id UUID        NOT NULL REFERENCES honua.studio_content_versions(version_id) ON DELETE CASCADE,
    pointer           TEXT        NOT NULL,
    current_version_id UUID       NULL,
    published_version_id UUID     NULL,
    requested_by      TEXT        NULL,
    reason            TEXT        NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_rollback_requests_pointer
        CHECK (pointer IN ('current','published','both'))
);

CREATE INDEX IF NOT EXISTS idx_studio_rollback_requests_item_created
    ON honua.studio_rollback_requests (item_id, created_at DESC);

COMMENT ON TABLE honua.studio_content_versions IS
    'Append-only immutable Studio package content versions (#1180).';
