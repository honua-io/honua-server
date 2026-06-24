-- Migration: 067_CreateFieldExportRecords.sql
-- Back-office field export packages (#1160). Durable, audited record of each
-- generated field export package (format, filter, record count, requester) layered
-- over the existing honua.form_submissions / honua.field_submission_reviews record
-- set without mutating submissions or review state.

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.field_export_records (
    export_id     UUID        NOT NULL PRIMARY KEY,
    format        TEXT        NOT NULL,
    record_count  BIGINT      NOT NULL DEFAULT 0,
    filter        JSONB       NOT NULL DEFAULT '{}'::jsonb,
    requested_by  TEXT        NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT field_export_records_valid_format
        CHECK (format IN ('geojson', 'csv'))
);

CREATE INDEX IF NOT EXISTS idx_field_export_records_created
    ON honua.field_export_records(created_at DESC);
