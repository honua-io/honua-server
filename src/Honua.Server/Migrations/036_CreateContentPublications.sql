-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Content publication registry for Studio-generated maps, dashboards, reports, and
-- generated apps (#1183). The registry owns the published route, active revision,
-- rollback pointer, share/embed/public-link policy, dependency graph, provenance, and
-- audit trail so Console no longer treats publish/rollback/share/embed as local UI state.
--
-- Two stores back the registry:
--   * content_publication_versions: append-only immutable content versions.
--   * content_publication_routes:   the single mutable route pointer per publication.
--   * content_publication_events:   append-only operation/audit event log.
-- Policy/dependency/provenance sidecars are stored as JSONB while the contract is alpha,
-- with targeted indexes for the hot lookups (route slug, publication id, kind,
-- source package id, source job id, and public-link containment).

-- ---------------------------------------------------------------------------
-- Append-only immutable versions.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS honua.content_publication_versions (
    version_id               UUID         NOT NULL PRIMARY KEY,
    publication_id           UUID         NOT NULL,
    revision                 BIGINT       NOT NULL,
    kind                     TEXT         NOT NULL,
    route_slug               TEXT         NOT NULL,
    route_path               TEXT         NOT NULL,
    title                    TEXT         NULL,
    source_content_id        TEXT         NULL,
    source_package_id        TEXT         NULL,
    content_hash             TEXT         NULL,
    content_version_id       TEXT         NULL,
    source_metadata_revision BIGINT       NULL,
    source_metadata_etag     TEXT         NULL,
    app_manifest_id          TEXT         NULL,
    app_bundle_artifact_id   TEXT         NULL,
    default_view_bbox        JSONB        NULL,
    policy                   JSONB        NOT NULL,
    dependencies             JSONB        NOT NULL DEFAULT '[]'::jsonb,
    provenance               JSONB        NOT NULL DEFAULT '[]'::jsonb,
    job_id                   TEXT         NULL,
    operation_id             TEXT         NULL,
    correlation_id           TEXT         NULL,
    audit_ref                TEXT         NULL,
    created_by               TEXT         NOT NULL,
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT content_publication_versions_kind CHECK (kind IN ('map','dashboard','report','generated-app')),
    CONSTRAINT content_publication_versions_revision_unique UNIQUE (publication_id, revision)
);

-- Append-only enforcement: block UPDATE / DELETE so historical content versions
-- (and their provenance) cannot be rewritten. Rollback and republish move the route
-- pointer only; they never mutate a version row.
DROP RULE IF EXISTS content_publication_versions_no_update ON honua.content_publication_versions;
CREATE RULE content_publication_versions_no_update AS ON UPDATE TO honua.content_publication_versions DO INSTEAD NOTHING;
DROP RULE IF EXISTS content_publication_versions_no_delete ON honua.content_publication_versions;
CREATE RULE content_publication_versions_no_delete AS ON DELETE TO honua.content_publication_versions DO INSTEAD NOTHING;

CREATE INDEX IF NOT EXISTS idx_content_publication_versions_pub
    ON honua.content_publication_versions (publication_id, revision DESC);
CREATE INDEX IF NOT EXISTS idx_content_publication_versions_slug
    ON honua.content_publication_versions (route_slug);
CREATE INDEX IF NOT EXISTS idx_content_publication_versions_kind
    ON honua.content_publication_versions (kind);
CREATE INDEX IF NOT EXISTS idx_content_publication_versions_source_package
    ON honua.content_publication_versions (source_package_id)
    WHERE source_package_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_content_publication_versions_job
    ON honua.content_publication_versions (job_id)
    WHERE job_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Mutable route pointer (exactly one per publication).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS honua.content_publication_routes (
    publication_id             UUID         NOT NULL PRIMARY KEY,
    route_slug                 TEXT         NOT NULL,
    route_path                 TEXT         NOT NULL,
    kind                       TEXT         NOT NULL,
    active_version_id          UUID         NOT NULL,
    active_revision            BIGINT       NOT NULL,
    previous_version_id        UUID         NULL,
    rollback_target_version_id UUID         NULL,
    lifecycle                  TEXT         NOT NULL DEFAULT 'active',
    policy                     JSONB        NOT NULL,
    generation                 BIGINT       NOT NULL DEFAULT 1,
    etag                       TEXT         NOT NULL,
    updated_by                 TEXT         NOT NULL,
    updated_at                 TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_at                 TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT content_publication_routes_kind CHECK (kind IN ('map','dashboard','report','generated-app')),
    CONSTRAINT content_publication_routes_lifecycle CHECK (lifecycle IN ('active','suspended','archived'))
);

-- A route slug resolves to exactly one publication.
CREATE UNIQUE INDEX IF NOT EXISTS idx_content_publication_routes_slug
    ON honua.content_publication_routes (route_slug);
CREATE INDEX IF NOT EXISTS idx_content_publication_routes_kind
    ON honua.content_publication_routes (kind);
CREATE INDEX IF NOT EXISTS idx_content_publication_routes_active_version
    ON honua.content_publication_routes (active_version_id);
-- Containment index supports public-link id/hash lookups within the policy sidecar.
CREATE INDEX IF NOT EXISTS idx_content_publication_routes_policy
    ON honua.content_publication_routes USING GIN (policy jsonb_path_ops);

-- ---------------------------------------------------------------------------
-- Append-only event / audit log.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS honua.content_publication_events (
    event_seq       BIGSERIAL    NOT NULL PRIMARY KEY,
    event_id        UUID         NOT NULL UNIQUE,
    publication_id  UUID         NOT NULL,
    operation       TEXT         NOT NULL,
    version_id      UUID         NULL,
    revision        BIGINT       NULL,
    route_slug      TEXT         NOT NULL,
    actor           TEXT         NOT NULL,
    correlation_id  TEXT         NULL,
    detail          TEXT         NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT content_publication_events_operation CHECK (operation IN (
        'publish','republish','rollback','policy-update',
        'public-link-create','public-link-revoke','public-link-denied','embed-denied'))
);

DROP RULE IF EXISTS content_publication_events_no_update ON honua.content_publication_events;
CREATE RULE content_publication_events_no_update AS ON UPDATE TO honua.content_publication_events DO INSTEAD NOTHING;
DROP RULE IF EXISTS content_publication_events_no_delete ON honua.content_publication_events;
CREATE RULE content_publication_events_no_delete AS ON DELETE TO honua.content_publication_events DO INSTEAD NOTHING;

CREATE INDEX IF NOT EXISTS idx_content_publication_events_pub
    ON honua.content_publication_events (publication_id, event_seq DESC);

COMMENT ON TABLE honua.content_publication_versions IS
    'Append-only immutable content publication versions for Studio-generated artifacts (#1183).';
COMMENT ON TABLE honua.content_publication_routes IS
    'Mutable server-owned route pointer (active version, rollback pointer, policy) per publication (#1183).';
COMMENT ON TABLE honua.content_publication_events IS
    'Append-only content publication operation/audit event log (#1183).';
