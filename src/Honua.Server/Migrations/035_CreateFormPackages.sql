-- Migration: 035_CreateFormPackages.sql
-- Server-owned form package lifecycle, submission idempotency, and attachment outcome records (#1184).

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.form_packages (
    form_id                   TEXT        NOT NULL PRIMARY KEY,
    title                     TEXT        NOT NULL,
    service_id                TEXT        NOT NULL,
    layer_id                  INT         NOT NULL,
    current_draft_version     INT,
    current_published_version INT,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_form_packages_target
    ON honua.form_packages(service_id, layer_id);

CREATE INDEX IF NOT EXISTS idx_form_packages_current_published
    ON honua.form_packages(current_published_version)
    WHERE current_published_version IS NOT NULL;

CREATE TABLE IF NOT EXISTS honua.form_package_versions (
    form_id               TEXT        NOT NULL REFERENCES honua.form_packages(form_id) ON DELETE CASCADE,
    version               INT         NOT NULL,
    status                TEXT        NOT NULL,
    package_json          JSONB       NOT NULL,
    validation_json       JSONB,
    content_hash          TEXT        NOT NULL,
    policy_hash           TEXT        NOT NULL,
    etag                  TEXT        NOT NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            TEXT,
    published_at          TIMESTAMPTZ,
    published_by          TEXT,
    reopened_from_version INT,
    PRIMARY KEY (form_id, version),
    CONSTRAINT form_package_versions_valid_status
        CHECK (status IN ('draft', 'published', 'archived'))
);

CREATE INDEX IF NOT EXISTS idx_form_package_versions_status
    ON honua.form_package_versions(status, form_id, version);

CREATE OR REPLACE FUNCTION honua.prevent_published_form_package_version_mutation()
RETURNS trigger AS $$
BEGIN
    IF OLD.status = 'published' THEN
        RAISE EXCEPTION 'Published form package versions are immutable';
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_form_package_versions_immutable
    ON honua.form_package_versions;

CREATE TRIGGER trg_form_package_versions_immutable
    BEFORE UPDATE OR DELETE ON honua.form_package_versions
    FOR EACH ROW
    EXECUTE FUNCTION honua.prevent_published_form_package_version_mutation();

CREATE TABLE IF NOT EXISTS honua.form_submissions (
    submission_id     UUID        NOT NULL PRIMARY KEY,
    idempotency_key   TEXT,
    actor_hash        TEXT        NOT NULL,
    form_id           TEXT        NOT NULL,
    form_version      INT         NOT NULL,
    service_id        TEXT        NOT NULL,
    layer_id          INT         NOT NULL,
    target_feature_id BIGINT,
    operation         TEXT        NOT NULL,
    status            TEXT        NOT NULL,
    package_hash      TEXT        NOT NULL,
    policy_hash       TEXT        NOT NULL,
    request_hash      TEXT        NOT NULL,
    request_summary   JSONB       NOT NULL,
    response_payload  JSONB,
    validation_json   JSONB,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at      TIMESTAMPTZ,
    CONSTRAINT form_submissions_valid_status
        CHECK (status IN ('pending', 'accepted', 'rejected', 'failed'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_form_submissions_idempotency
    ON honua.form_submissions(form_id, form_version, actor_hash, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_form_submissions_form_time
    ON honua.form_submissions(form_id, form_version, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_form_submissions_target
    ON honua.form_submissions(service_id, layer_id, target_feature_id);

CREATE TABLE IF NOT EXISTS honua.form_submission_attachments (
    id                   BIGSERIAL   NOT NULL PRIMARY KEY,
    submission_id        UUID        NOT NULL REFERENCES honua.form_submissions(submission_id) ON DELETE CASCADE,
    field_id             TEXT,
    client_attachment_id TEXT,
    filename             TEXT,
    content_type         TEXT,
    size_bytes           BIGINT,
    sha256               TEXT,
    part_name            TEXT,
    attachment_id        BIGINT,
    privacy_policy       JSONB       NOT NULL,
    status               TEXT        NOT NULL,
    failure_reason       TEXT,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_form_submission_attachments_submission
    ON honua.form_submission_attachments(submission_id);
