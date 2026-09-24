-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 122_CreateWorkflowPackages.sql
-- Description: Durable workflow package drafts, immutable versions, and publications.
--              PostGIS is the 2026.1 default for IWorkflowPackageStore so these records
--              survive a process restart and are visible to peer replicas (#3589).
-- Dependencies: honua schema from migration 001.

CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_packages (
    package_id      TEXT        NOT NULL PRIMARY KEY,
    name            TEXT        NOT NULL,
    description     TEXT        NULL,
    namespace       TEXT        NULL,
    graph_json      JSONB       NOT NULL,
    latest_version  INT         NULL,
    created_at      TIMESTAMPTZ NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL,
    created_by      TEXT        NULL,
    updated_by      TEXT        NULL,
    metadata_json   JSONB       NOT NULL
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_package_versions (
    package_id      TEXT        NOT NULL REFERENCES $HonuaSchema$.workflow_packages(package_id) ON DELETE CASCADE,
    version         INT         NOT NULL,
    schema_version  TEXT        NOT NULL,
    package_hash    TEXT        NOT NULL,
    graph_json      JSONB       NOT NULL,
    validation_json JSONB       NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL,
    created_by      TEXT        NULL,
    PRIMARY KEY (package_id, version)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.workflow_publications (
    publication_id          TEXT        NOT NULL PRIMARY KEY,
    package_id              TEXT        NOT NULL,
    package_version         INT         NOT NULL,
    package_hash            TEXT        NOT NULL,
    target                  TEXT        NOT NULL,
    status                  TEXT        NOT NULL,
    process_id              TEXT        NULL,
    schedule_json           JSONB       NULL,
    workflow_definition_id  TEXT        NULL,
    endpoint_path           TEXT        NULL,
    eligibility_json        JSONB       NOT NULL,
    created_at              TIMESTAMPTZ NOT NULL,
    created_by              TEXT        NULL,
    provenance_json         JSONB       NOT NULL,
    CONSTRAINT workflow_publications_version_fk
        FOREIGN KEY (package_id, package_version)
        REFERENCES $HonuaSchema$.workflow_package_versions(package_id, version)
);

CREATE INDEX IF NOT EXISTS idx_workflow_publications_package
    ON $HonuaSchema$.workflow_publications(package_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_workflow_publications_definition
    ON $HonuaSchema$.workflow_publications(workflow_definition_id)
    WHERE workflow_definition_id IS NOT NULL;
