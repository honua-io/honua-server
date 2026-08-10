-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Forward-only repair for installations that selected a non-default Database:Schema after the
-- original Studio lifecycle migration was journaled (#3067). Fresh installations provision these
-- objects in migration 035; existing installations need this idempotent migration because DbUp
-- does not replay an already-journaled script after its schema qualification is corrected.
CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_content_items (
    item_id              UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    package_key          TEXT        NOT NULL,
    workspace_id         TEXT        NULL,
    family               TEXT        NOT NULL,
    current_version_id   UUID        NULL,
    published_version_id UUID        NULL,
    owner_id             TEXT        NULL,
    created_by           TEXT        NULL,
    updated_by           TEXT        NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_content_items_family
        CHECK (family IN ('query','analysis','map','dashboard','report','form','app','workflow','gp','etl'))
);

ALTER TABLE $HonuaSchema$.studio_content_items
    ADD COLUMN IF NOT EXISTS owner_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_content_items_workspace_key
    ON $HonuaSchema$.studio_content_items (
        (COALESCE(NULLIF(BTRIM(workspace_id), ''), '')),
        family,
        package_key
    );

CREATE INDEX IF NOT EXISTS idx_studio_content_items_updated
    ON $HonuaSchema$.studio_content_items (updated_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_package_drafts (
    draft_id        UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id         UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
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
    ON $HonuaSchema$.studio_package_drafts (item_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_owner
    ON $HonuaSchema$.studio_package_drafts (owner_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_content_versions (
    version_id      UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id         UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
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
    ON $HonuaSchema$.studio_content_versions (item_id, version_number);

CREATE INDEX IF NOT EXISTS idx_studio_content_versions_item_hash
    ON $HonuaSchema$.studio_content_versions (item_id, content_hash);

CREATE INDEX IF NOT EXISTS idx_studio_content_versions_item_created
    ON $HonuaSchema$.studio_content_versions (item_id, created_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_content_version_dependencies (
    version_id            UUID    NOT NULL REFERENCES $HonuaSchema$.studio_content_versions(version_id) ON DELETE CASCADE,
    item_id               UUID    NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
    dependency_kind       TEXT    NOT NULL,
    dependency_ref        TEXT    NOT NULL,
    dependency_version_id TEXT    NULL,
    required              BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_studio_content_version_dependencies_unique
    ON $HonuaSchema$.studio_content_version_dependencies (
        version_id,
        dependency_kind,
        dependency_ref,
        (COALESCE(dependency_version_id, ''))
    );

CREATE INDEX IF NOT EXISTS idx_studio_content_version_dependencies_ref
    ON $HonuaSchema$.studio_content_version_dependencies (dependency_kind, dependency_ref);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_publication_requests (
    request_id              UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id                 UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
    version_id              UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_versions(version_id) ON DELETE CASCADE,
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
    ON $HonuaSchema$.studio_publication_requests (item_id, created_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_rollback_requests (
    request_id           UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    item_id              UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_items(item_id) ON DELETE CASCADE,
    target_version_id    UUID        NOT NULL REFERENCES $HonuaSchema$.studio_content_versions(version_id) ON DELETE CASCADE,
    pointer              TEXT        NOT NULL,
    current_version_id   UUID        NULL,
    published_version_id UUID        NULL,
    requested_by         TEXT        NULL,
    reason               TEXT        NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT studio_rollback_requests_pointer
        CHECK (pointer IN ('current','published','both'))
);

CREATE INDEX IF NOT EXISTS idx_studio_rollback_requests_item_created
    ON $HonuaSchema$.studio_rollback_requests (item_id, created_at DESC);

UPDATE $HonuaSchema$.studio_content_items AS item
SET owner_id = COALESCE(
    (
        SELECT NULLIF(BTRIM(draft.owner_id), '')
        FROM $HonuaSchema$.studio_package_drafts AS draft
        WHERE draft.item_id = item.item_id
          AND NULLIF(BTRIM(draft.owner_id), '') IS NOT NULL
        ORDER BY draft.created_at ASC, draft.draft_id ASC
        LIMIT 1
    ),
    item.created_by
)
WHERE item.owner_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_studio_content_items_owner_list
    ON $HonuaSchema$.studio_content_items (owner_id, updated_at DESC, item_id DESC)
    WHERE owner_id IS NOT NULL;

COMMENT ON TABLE $HonuaSchema$.studio_content_versions IS
    'Append-only immutable Studio package content versions (#1180).';
