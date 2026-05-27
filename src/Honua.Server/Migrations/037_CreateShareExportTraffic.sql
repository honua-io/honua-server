-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 037_CreateShareExportTraffic.sql
-- Description: Adds server-owned Console Share export definitions, export run
--              history, and Share traffic read-projection buckets (#1216).
-- Dependencies: honua schema.

CREATE TABLE IF NOT EXISTS honua.share_export_definitions (
    export_id           TEXT        PRIMARY KEY,
    resource_id         TEXT        NULL,
    service_name        TEXT        NOT NULL,
    layer_id            INTEGER     NOT NULL,
    display_name        TEXT        NULL,
    destination_type    TEXT        NOT NULL,
    destination_status  TEXT        NOT NULL,
    destination_config  JSONB       NOT NULL DEFAULT '{}'::jsonb,
    format              TEXT        NOT NULL,
    schedule            TEXT        NOT NULL,
    schedule_state      TEXT        NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL,
    updated_at          TIMESTAMPTZ NOT NULL,
    last_run_at         TIMESTAMPTZ NULL,
    next_run_at         TIMESTAMPTZ NULL,
    CONSTRAINT share_export_definitions_service_not_empty CHECK (length(service_name) > 0),
    CONSTRAINT share_export_definitions_layer_nonnegative CHECK (layer_id >= 0),
    CONSTRAINT share_export_definitions_destination_type CHECK (destination_type IN ('S3', 'Sftp', 'Webhook', 'AuditSnapshot')),
    CONSTRAINT share_export_definitions_destination_status CHECK (destination_status IN ('Supported', 'Unsupported', 'NotConfigured')),
    CONSTRAINT share_export_definitions_config_object CHECK (jsonb_typeof(destination_config) = 'object'),
    CONSTRAINT share_export_definitions_format_not_empty CHECK (length(format) > 0),
    CONSTRAINT share_export_definitions_schedule_not_empty CHECK (length(schedule) > 0),
    CONSTRAINT share_export_definitions_schedule_state CHECK (schedule_state IN ('Active', 'Paused'))
);

CREATE INDEX IF NOT EXISTS idx_share_export_definitions_updated
    ON honua.share_export_definitions (updated_at DESC, export_id DESC);

CREATE INDEX IF NOT EXISTS idx_share_export_definitions_resource
    ON honua.share_export_definitions (resource_id, updated_at DESC)
    WHERE resource_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_share_export_definitions_service_layer
    ON honua.share_export_definitions (service_name, layer_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_share_export_definitions_destination
    ON honua.share_export_definitions (destination_type, destination_status, updated_at DESC);

CREATE TABLE IF NOT EXISTS honua.share_export_runs (
    run_id            TEXT        PRIMARY KEY,
    export_id         TEXT        NOT NULL REFERENCES honua.share_export_definitions(export_id) ON DELETE CASCADE,
    trigger_kind      TEXT        NOT NULL,
    status            TEXT        NOT NULL,
    job_run_id        TEXT        NULL,
    triggered_at      TIMESTAMPTZ NOT NULL,
    started_at        TIMESTAMPTZ NULL,
    completed_at      TIMESTAMPTZ NULL,
    target_summary    TEXT        NULL,
    result_artifacts  TEXT[]      NOT NULL DEFAULT '{}'::text[],
    last_error        TEXT        NULL,
    CONSTRAINT share_export_runs_trigger_kind CHECK (trigger_kind IN ('Scheduled', 'Manual')),
    CONSTRAINT share_export_runs_status CHECK (status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled'))
);

CREATE INDEX IF NOT EXISTS idx_share_export_runs_export_triggered
    ON honua.share_export_runs (export_id, triggered_at DESC, run_id DESC);

CREATE INDEX IF NOT EXISTS idx_share_export_runs_job
    ON honua.share_export_runs (job_run_id)
    WHERE job_run_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS honua.share_traffic_buckets (
    traffic_id          BIGSERIAL   PRIMARY KEY,
    bucket_start        TIMESTAMPTZ NOT NULL,
    resource_id         TEXT        NULL,
    service_name        TEXT        NULL,
    layer_id            INTEGER     NULL,
    public_count        BIGINT      NOT NULL DEFAULT 0,
    public_link_count   BIGINT      NOT NULL DEFAULT 0,
    embed_count         BIGINT      NOT NULL DEFAULT 0,
    open_data_count     BIGINT      NOT NULL DEFAULT 0,
    dcat_count          BIGINT      NOT NULL DEFAULT 0,
    stac_count          BIGINT      NOT NULL DEFAULT 0,
    export_count        BIGINT      NOT NULL DEFAULT 0,
    CONSTRAINT share_traffic_buckets_layer_nonnegative CHECK (layer_id IS NULL OR layer_id >= 0),
    CONSTRAINT share_traffic_buckets_counts_nonnegative CHECK (
        public_count >= 0
        AND public_link_count >= 0
        AND embed_count >= 0
        AND open_data_count >= 0
        AND dcat_count >= 0
        AND stac_count >= 0
        AND export_count >= 0
    )
);

CREATE INDEX IF NOT EXISTS idx_share_traffic_buckets_time
    ON honua.share_traffic_buckets (bucket_start);

CREATE INDEX IF NOT EXISTS idx_share_traffic_buckets_item_time
    ON honua.share_traffic_buckets (service_name, layer_id, bucket_start)
    WHERE service_name IS NOT NULL AND layer_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_share_traffic_buckets_resource_time
    ON honua.share_traffic_buckets (resource_id, bucket_start)
    WHERE resource_id IS NOT NULL;

COMMENT ON TABLE honua.share_export_definitions IS
    'Server-owned scheduled Share export definitions for Console Share panels (#1216).';
COMMENT ON TABLE honua.share_export_runs IS
    'Append-mostly Share export run history, including nullable job_run_id deep links to Operate jobs (#1216).';
COMMENT ON TABLE honua.share_traffic_buckets IS
    'Share traffic read-projection buckets for aggregate and per-item Console panels (#1216).';
