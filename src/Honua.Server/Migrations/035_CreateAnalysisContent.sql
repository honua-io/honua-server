-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable saved-query and analysis-package content versions plus job artifact
-- metadata for downstream map/dashboard/report/app/workflow bindings (#1182).

CREATE TABLE IF NOT EXISTS honua.analysis_content_items (
    item_id            TEXT        PRIMARY KEY,
    kind               TEXT        NOT NULL,
    name               TEXT        NOT NULL,
    title              TEXT        NULL,
    owner_id           TEXT        NULL,
    visibility         TEXT        NOT NULL DEFAULT 'organization',
    current_version    INT         NOT NULL,
    current_version_id TEXT        NOT NULL,
    lifecycle          TEXT        NOT NULL DEFAULT 'Active',
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by         TEXT        NULL,
    CONSTRAINT analysis_content_items_kind
        CHECK (kind IN ('SavedQuery', 'AnalysisPackage')),
    CONSTRAINT analysis_content_items_lifecycle
        CHECK (lifecycle IN ('Active', 'Archived', 'Deleted')),
    CONSTRAINT analysis_content_items_version_positive
        CHECK (current_version > 0),
    CONSTRAINT analysis_content_items_name_not_empty
        CHECK (length(btrim(name)) > 0)
);

CREATE INDEX IF NOT EXISTS idx_analysis_content_items_kind_updated
    ON honua.analysis_content_items (kind, updated_at DESC, item_id);

CREATE INDEX IF NOT EXISTS idx_analysis_content_items_owner_updated
    ON honua.analysis_content_items (owner_id, updated_at DESC, item_id);

CREATE TABLE IF NOT EXISTS honua.analysis_content_versions (
    version_id    TEXT        PRIMARY KEY,
    item_id       TEXT        NOT NULL REFERENCES honua.analysis_content_items(item_id) ON DELETE CASCADE,
    version       INT         NOT NULL,
    kind          TEXT        NOT NULL,
    content_hash  TEXT        NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by    TEXT        NULL,
    version_body  JSONB       NOT NULL,
    CONSTRAINT analysis_content_versions_item_version UNIQUE (item_id, version),
    CONSTRAINT analysis_content_versions_kind
        CHECK (kind IN ('SavedQuery', 'AnalysisPackage')),
    CONSTRAINT analysis_content_versions_version_positive
        CHECK (version > 0)
);

CREATE INDEX IF NOT EXISTS idx_analysis_content_versions_item_created
    ON honua.analysis_content_versions (item_id, version DESC);

CREATE INDEX IF NOT EXISTS idx_analysis_content_versions_hash
    ON honua.analysis_content_versions (content_hash);

CREATE TABLE IF NOT EXISTS honua.analysis_result_artifacts (
    artifact_id       TEXT        PRIMARY KEY,
    result_package_id TEXT        NOT NULL,
    job_id            TEXT        NOT NULL,
    source_item_id    TEXT        NOT NULL,
    source_version    INT         NOT NULL,
    source_version_id TEXT        NOT NULL,
    kind              TEXT        NOT NULL,
    retention_state   TEXT        NOT NULL DEFAULT 'Retained',
    promotion_state   TEXT        NOT NULL DEFAULT 'None',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at        TIMESTAMPTZ NULL,
    artifact_body     JSONB       NOT NULL,
    CONSTRAINT analysis_result_artifacts_source_version_positive
        CHECK (source_version > 0),
    CONSTRAINT analysis_result_artifacts_retention
        CHECK (retention_state IN ('Preview', 'Retained', 'Promoted', 'Expired')),
    CONSTRAINT analysis_result_artifacts_promotion
        CHECK (promotion_state IN ('None', 'Promoted'))
);

CREATE INDEX IF NOT EXISTS idx_analysis_result_artifacts_job_created
    ON honua.analysis_result_artifacts (job_id, created_at, artifact_id);

CREATE INDEX IF NOT EXISTS idx_analysis_result_artifacts_source
    ON honua.analysis_result_artifacts (source_item_id, source_version, artifact_id);

CREATE INDEX IF NOT EXISTS idx_analysis_result_artifacts_retention
    ON honua.analysis_result_artifacts (retention_state, expires_at);

COMMENT ON TABLE honua.analysis_content_items IS
    'Durable roots for saved-query and analysis-package content items (#1182).';
COMMENT ON TABLE honua.analysis_content_versions IS
    'Immutable analysis content versions (#1182).';
COMMENT ON TABLE honua.analysis_result_artifacts IS
    'Stable artifact metadata produced by saved-query previews and analysis jobs (#1182).';
