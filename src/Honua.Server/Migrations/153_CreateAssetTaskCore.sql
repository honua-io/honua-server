-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Task-first asset management core (ADR-0084). Customer layers are never altered: profiles map their
-- columns, and the domain-owned summary sidecar carries the per-asset revision and observation watermarks.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.asset_profiles (
    profile_id               UUID PRIMARY KEY,
    service_id               TEXT NOT NULL,
    layer_id                 INTEGER NOT NULL,
    name                     TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    geometry_type            TEXT NOT NULL CHECK (geometry_type IN ('point', 'line', 'polygon')),
    asset_id_field           TEXT NOT NULL,
    condition_field          TEXT,
    status_field             TEXT,
    last_inspected_field     TEXT,
    last_serviced_field      TEXT,
    condition_labels         JSONB NOT NULL CHECK (jsonb_typeof(condition_labels) = 'array'
                                 AND jsonb_array_length(condition_labels) = 5),
    inspection_form_id       TEXT,
    service_form_id          TEXT,
    state                    TEXT NOT NULL CHECK (state IN ('draft', 'active', 'archived')),
    version                  BIGINT NOT NULL CHECK (version > 0),
    created_by               TEXT NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL,
    updated_by               TEXT NOT NULL,
    updated_at               TIMESTAMPTZ NOT NULL,
    CONSTRAINT asset_profiles_one_per_layer UNIQUE (service_id, layer_id)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.asset_summaries (
    profile_id               UUID NOT NULL REFERENCES $HonuaSchema$.asset_profiles (profile_id),
    asset_id                 TEXT NOT NULL,
    revision                 BIGINT NOT NULL CHECK (revision > 0),
    condition                SMALLINT CHECK (condition BETWEEN 1 AND 5),
    last_inspected_at        TIMESTAMPTZ,
    inspection_observed_at   TIMESTAMPTZ,
    last_serviced_at         TIMESTAMPTZ,
    service_observed_at      TIMESTAMPTZ,
    updated_at               TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (profile_id, asset_id)
);

-- object_id is the stable feature ID when tasks are projected as a query layer.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.work_tasks (
    task_id                  UUID PRIMARY KEY,
    object_id                BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    service_id               TEXT NOT NULL,
    task_type                TEXT NOT NULL CHECK (task_type IN ('inspection', 'maintenance', 'repair', 'request')),
    status                   TEXT NOT NULL CHECK (status IN
                                 ('unassigned', 'assigned', 'in_progress', 'completed', 'cancelled')),
    version                  BIGINT NOT NULL CHECK (version > 0),
    profile_id               UUID REFERENCES $HonuaSchema$.asset_profiles (profile_id),
    asset_id                 TEXT,
    programme_id             UUID,
    cycle_key                TEXT,
    geometry                 GEOMETRY NOT NULL,
    geometry_source          TEXT NOT NULL CHECK (geometry_source IN ('asset', 'explicit')),
    title                    TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 300),
    assignee                 TEXT,
    priority                 TEXT,
    due_at                   TIMESTAMPTZ,
    form_id                  TEXT NOT NULL,
    form_version             INTEGER NOT NULL CHECK (form_version > 0),
    completion               JSONB,
    created_by               TEXT NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL,
    updated_at               TIMESTAMPTZ NOT NULL,
    started_at               TIMESTAMPTZ,
    completed_at             TIMESTAMPTZ,
    cancelled_at             TIMESTAMPTZ,
    cancel_reason            TEXT,
    CONSTRAINT work_tasks_asset_reference CHECK ((profile_id IS NULL) = (asset_id IS NULL)),
    CONSTRAINT work_tasks_inspection_needs_asset CHECK (task_type <> 'inspection' OR asset_id IS NOT NULL),
    CONSTRAINT work_tasks_assigned_has_assignee CHECK (status NOT IN ('assigned', 'in_progress') OR assignee IS NOT NULL),
    CONSTRAINT work_tasks_unassigned_has_none CHECK (status <> 'unassigned' OR assignee IS NULL),
    CONSTRAINT work_tasks_cancel_reason CHECK (status <> 'cancelled' OR cancel_reason IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS idx_work_tasks_service_status_due
    ON $HonuaSchema$.work_tasks (service_id, status, due_at, task_id);

CREATE INDEX IF NOT EXISTS idx_work_tasks_assignee
    ON $HonuaSchema$.work_tasks (assignee, status) WHERE assignee IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_work_tasks_asset
    ON $HonuaSchema$.work_tasks (profile_id, asset_id) WHERE asset_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_work_tasks_geometry
    ON $HonuaSchema$.work_tasks USING GIST (geometry);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.work_task_history (
    event_id                 UUID PRIMARY KEY,
    task_id                  UUID NOT NULL REFERENCES $HonuaSchema$.work_tasks (task_id),
    command_id               UUID NOT NULL,
    action                   TEXT NOT NULL,
    old_version              BIGINT,
    new_version              BIGINT NOT NULL,
    old_status               TEXT,
    new_status               TEXT NOT NULL,
    actor                    TEXT NOT NULL,
    recorded_at              TIMESTAMPTZ NOT NULL,
    observed_at              TIMESTAMPTZ,
    changed_fields           JSONB NOT NULL,
    reason                   TEXT,
    proposal_ref             TEXT,
    CONSTRAINT work_task_history_one_per_version UNIQUE (task_id, new_version)
);

-- A command ID is accepted once per service: an identical replay by the same actor returns the stored response;
-- any other reuse of the ID is a conflict.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.asset_command_receipts (
    service_id               TEXT NOT NULL,
    command_id               UUID NOT NULL,
    subject_kind             TEXT NOT NULL CHECK (subject_kind IN ('asset_profile', 'work_task')),
    subject_id               UUID NOT NULL,
    action                   TEXT NOT NULL,
    actor                    TEXT NOT NULL,
    request_sha256           TEXT NOT NULL CHECK (length(request_sha256) = 64),
    status_code              SMALLINT NOT NULL,
    response                 JSONB NOT NULL,
    recorded_at              TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (service_id, command_id)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.asset_inspections (
    inspection_id            UUID PRIMARY KEY,
    task_id                  UUID NOT NULL UNIQUE REFERENCES $HonuaSchema$.work_tasks (task_id),
    profile_id               UUID NOT NULL REFERENCES $HonuaSchema$.asset_profiles (profile_id),
    asset_id                 TEXT NOT NULL,
    form_id                  TEXT NOT NULL,
    form_version             INTEGER NOT NULL,
    condition                SMALLINT CHECK (condition BETWEEN 1 AND 5),
    answers                  JSONB NOT NULL,
    notes                    TEXT,
    attachments              JSONB NOT NULL,
    inspector                TEXT NOT NULL,
    observed_at              TIMESTAMPTZ NOT NULL,
    recorded_at              TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_asset_inspections_asset
    ON $HonuaSchema$.asset_inspections (profile_id, asset_id, observed_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.asset_service_records (
    service_record_id        UUID PRIMARY KEY,
    task_id                  UUID NOT NULL UNIQUE REFERENCES $HonuaSchema$.work_tasks (task_id),
    profile_id               UUID REFERENCES $HonuaSchema$.asset_profiles (profile_id),
    asset_id                 TEXT,
    location                 GEOMETRY NOT NULL,
    service_type             TEXT NOT NULL,
    form_id                  TEXT NOT NULL,
    form_version             INTEGER NOT NULL,
    answers                  JSONB NOT NULL,
    notes                    TEXT,
    attachments              JSONB NOT NULL,
    actor                    TEXT NOT NULL,
    observed_at              TIMESTAMPTZ NOT NULL,
    recorded_at              TIMESTAMPTZ NOT NULL,
    CONSTRAINT asset_service_records_asset_reference CHECK ((profile_id IS NULL) = (asset_id IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_asset_service_records_asset
    ON $HonuaSchema$.asset_service_records (profile_id, asset_id, observed_at DESC) WHERE asset_id IS NOT NULL;

CREATE OR REPLACE FUNCTION $HonuaSchema$.reject_append_only_mutation()
RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_work_task_history_append_only
    BEFORE UPDATE OR DELETE ON $HonuaSchema$.work_task_history
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.reject_append_only_mutation();

CREATE OR REPLACE TRIGGER trg_asset_command_receipts_append_only
    BEFORE UPDATE OR DELETE ON $HonuaSchema$.asset_command_receipts
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.reject_append_only_mutation();

CREATE OR REPLACE TRIGGER trg_asset_inspections_append_only
    BEFORE UPDATE OR DELETE ON $HonuaSchema$.asset_inspections
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.reject_append_only_mutation();

CREATE OR REPLACE TRIGGER trg_asset_service_records_append_only
    BEFORE UPDATE OR DELETE ON $HonuaSchema$.asset_service_records
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.reject_append_only_mutation();
