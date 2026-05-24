-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Metadata v2 release packages for GitOps-safe semantic promotion (#1163).
-- Packages reference Metadata v2 revisions and serialize changed semantic entries as JSONB while
-- the release contract is alpha. Secret material is never stored here; bindings carry references only.

CREATE TABLE IF NOT EXISTS honua.metadata_v2_release_packages (
    package_id          UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    package_key         TEXT        NOT NULL,
    package_namespace   TEXT        NULL,
    status              TEXT        NOT NULL DEFAULT 'draft',
    source_environment  TEXT        NOT NULL,
    source_revision     BIGINT      NOT NULL,
    source_etag         TEXT        NOT NULL,
    target_environments JSONB       NOT NULL,
    entries             JSONB       NOT NULL,
    package_metadata    JSONB       NOT NULL,
    created_by          TEXT        NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT metadata_v2_release_packages_status
        CHECK (status IN ('draft','ready','staged','superseded','cancelled'))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_metadata_v2_release_packages_key
    ON honua.metadata_v2_release_packages (
        (COALESCE(NULLIF(BTRIM(package_namespace), ''), '')),
        package_key
    );

CREATE INDEX IF NOT EXISTS idx_metadata_v2_release_packages_created
    ON honua.metadata_v2_release_packages (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_metadata_v2_release_packages_status
    ON honua.metadata_v2_release_packages (status);

COMMENT ON TABLE honua.metadata_v2_release_packages IS
    'GitOps-safe Metadata v2 release packages for semantic cross-environment promotion (#1163).';
