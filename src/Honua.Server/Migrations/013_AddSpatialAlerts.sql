-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 013_AddSpatialAlerts.sql
-- Description: Adds durable geofencing and spatial alerting tables, state tracking,
--              event history, and dispatch outbox infrastructure.
-- Dependencies: Requires honua schema and change-tracking generation log
--               from 012_AddReplicationDurability.sql.

-- Geofence zones (geometry envelopes used by alert rules)
CREATE TABLE IF NOT EXISTS honua.alert_zones (
    zone_id BIGSERIAL PRIMARY KEY,
    service_id TEXT NOT NULL,
    zone_name TEXT NOT NULL,
    geometry GEOMETRY(MULTIPOLYGON),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_zones_valid_service CHECK (length(service_id) > 0),
    CONSTRAINT alert_zones_valid_name CHECK (length(zone_name) > 0)
);

-- Alert rules mapped to a layer and optional zone
-- trigger_type: 1=enter, 2=exit, 3=dwell, 4=threshold
-- severity: info|warning|critical
-- edition_required: 1=pro, 2=enterprise
CREATE TABLE IF NOT EXISTS honua.alert_rules (
    rule_id BIGSERIAL PRIMARY KEY,
    service_id TEXT NOT NULL,
    layer_id INT NOT NULL,
    zone_id BIGINT NULL REFERENCES honua.alert_zones(zone_id) ON DELETE CASCADE,
    rule_name TEXT NOT NULL,
    trigger_type SMALLINT NOT NULL,
    conditions JSONB NOT NULL DEFAULT '{}'::jsonb,
    cooldown_seconds INT NOT NULL DEFAULT 0,
    severity TEXT NOT NULL DEFAULT 'warning',
    edition_required SMALLINT NOT NULL DEFAULT 1,
    channels TEXT[] NOT NULL DEFAULT '{}'::text[],
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_rules_valid_service CHECK (length(service_id) > 0),
    CONSTRAINT alert_rules_valid_name CHECK (length(rule_name) > 0),
    CONSTRAINT alert_rules_valid_trigger CHECK (trigger_type IN (1, 2, 3, 4)),
    CONSTRAINT alert_rules_valid_cooldown CHECK (cooldown_seconds >= 0),
    CONSTRAINT alert_rules_valid_severity CHECK (severity IN ('info', 'warning', 'critical')),
    CONSTRAINT alert_rules_valid_edition CHECK (edition_required IN (1, 2))
);

-- Per feature/rule state for transition-driven evaluation
CREATE TABLE IF NOT EXISTS honua.alert_state (
    rule_id BIGINT NOT NULL REFERENCES honua.alert_rules(rule_id) ON DELETE CASCADE,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    inside BOOLEAN NOT NULL DEFAULT FALSE,
    entered_at TIMESTAMPTZ,
    last_evaluated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_alert_at TIMESTAMPTZ,
    last_generation BIGINT NOT NULL DEFAULT 0,
    threshold_state JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (rule_id, layer_id, objectid)
);

-- Immutable alert event history
CREATE TABLE IF NOT EXISTS honua.alert_events (
    event_id BIGSERIAL PRIMARY KEY,
    dedupe_key TEXT NOT NULL,
    rule_id BIGINT NOT NULL REFERENCES honua.alert_rules(rule_id) ON DELETE CASCADE,
    zone_id BIGINT NULL REFERENCES honua.alert_zones(zone_id) ON DELETE SET NULL,
    service_id TEXT NOT NULL,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    trigger_type SMALLINT NOT NULL,
    generation BIGINT NOT NULL,
    severity TEXT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    payload JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_events_valid_dedupe_key CHECK (length(dedupe_key) > 0),
    CONSTRAINT alert_events_valid_trigger CHECK (trigger_type IN (1, 2, 3, 4)),
    CONSTRAINT alert_events_valid_generation CHECK (generation >= 0),
    CONSTRAINT alert_events_valid_severity CHECK (severity IN ('info', 'warning', 'critical'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_alert_events_dedupe_key
    ON honua.alert_events(dedupe_key);

-- Delivery outbox
-- channel_type: 1=webhook, 2=websocket, 3=email, 4=digest
-- status: 0=pending, 1=processing, 2=delivered, 3=failed, 4=dead_letter
CREATE TABLE IF NOT EXISTS honua.alert_dispatch (
    dispatch_id BIGSERIAL PRIMARY KEY,
    event_id BIGINT NOT NULL REFERENCES honua.alert_events(event_id) ON DELETE CASCADE,
    channel_type SMALLINT NOT NULL,
    destination TEXT,
    status SMALLINT NOT NULL DEFAULT 0,
    attempts INT NOT NULL DEFAULT 0,
    max_attempts INT NOT NULL DEFAULT 5,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_attempt_at TIMESTAMPTZ,
    delivered_at TIMESTAMPTZ,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_dispatch_valid_channel CHECK (channel_type IN (1, 2, 3, 4)),
    CONSTRAINT alert_dispatch_valid_status CHECK (status IN (0, 1, 2, 3, 4)),
    CONSTRAINT alert_dispatch_valid_attempts CHECK (attempts >= 0),
    CONSTRAINT alert_dispatch_valid_max_attempts CHECK (max_attempts > 0)
);

-- Persistent evaluator checkpoint and sweep metadata
CREATE TABLE IF NOT EXISTS honua.alert_worker_checkpoint (
    worker_name TEXT PRIMARY KEY,
    last_generation BIGINT NOT NULL DEFAULT 0,
    last_dwell_sweep_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_worker_checkpoint_valid_worker CHECK (length(worker_name) > 0),
    CONSTRAINT alert_worker_checkpoint_valid_generation CHECK (last_generation >= 0)
);

INSERT INTO honua.alert_worker_checkpoint(worker_name, last_generation)
VALUES ('evaluator', 0)
ON CONFLICT (worker_name) DO NOTHING;

-- Spatial/rule lookup indexes
CREATE INDEX IF NOT EXISTS idx_alert_zones_service_active
    ON honua.alert_zones(service_id)
    WHERE is_active = TRUE;

CREATE INDEX IF NOT EXISTS idx_alert_zones_geometry
    ON honua.alert_zones USING GIST(geometry);

CREATE INDEX IF NOT EXISTS idx_alert_rules_service_layer_active
    ON honua.alert_rules(service_id, layer_id)
    WHERE is_active = TRUE;

CREATE INDEX IF NOT EXISTS idx_alert_rules_zone
    ON honua.alert_rules(zone_id)
    WHERE zone_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_alert_state_inside_entered
    ON honua.alert_state(rule_id, inside, entered_at)
    WHERE inside = TRUE;

CREATE INDEX IF NOT EXISTS idx_alert_state_last_generation
    ON honua.alert_state(last_generation);

CREATE INDEX IF NOT EXISTS idx_alert_events_rule_generation
    ON honua.alert_events(rule_id, generation);

CREATE INDEX IF NOT EXISTS idx_alert_events_layer_objectid_generation
    ON honua.alert_events(layer_id, objectid, generation);

-- Claim-ready index for outbox workers using FOR UPDATE SKIP LOCKED
CREATE INDEX IF NOT EXISTS idx_alert_dispatch_claim
    ON honua.alert_dispatch(status, next_attempt_at, dispatch_id)
    WHERE status IN (0, 3);

CREATE INDEX IF NOT EXISTS idx_alert_dispatch_event
    ON honua.alert_dispatch(event_id);

-- Documentation
COMMENT ON TABLE honua.alert_zones IS 'Geofence zones used for spatial alert rule evaluation';
COMMENT ON TABLE honua.alert_rules IS 'Alert rule definitions with trigger, conditions, cooldown, severity, and channel metadata';
COMMENT ON TABLE honua.alert_state IS 'Per-rule, per-feature transition state used for enter/exit/dwell/threshold evaluation';
COMMENT ON TABLE honua.alert_events IS 'Immutable alert event history with generation and idempotent dedupe key';
COMMENT ON TABLE honua.alert_dispatch IS 'Outbox dispatch queue for alert delivery channels with retries and dead-letter lifecycle';
COMMENT ON TABLE honua.alert_worker_checkpoint IS 'Durable checkpoint for evaluator cursor and periodic dwell sweep state';

COMMENT ON COLUMN honua.alert_rules.trigger_type IS '1=enter, 2=exit, 3=dwell, 4=threshold';
COMMENT ON COLUMN honua.alert_rules.edition_required IS '1=pro, 2=enterprise';
COMMENT ON COLUMN honua.alert_dispatch.channel_type IS '1=webhook, 2=websocket, 3=email, 4=digest';
COMMENT ON COLUMN honua.alert_dispatch.status IS '0=pending, 1=processing, 2=delivered, 3=failed, 4=dead_letter';
COMMENT ON COLUMN honua.alert_events.dedupe_key IS 'Unique idempotency key for replay-safe event inserts';
