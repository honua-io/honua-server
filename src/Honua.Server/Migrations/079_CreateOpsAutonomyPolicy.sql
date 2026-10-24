-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 079_CreateOpsAutonomyPolicy.sql
-- Description: Persists graduated autonomy policy, global kill switch, track
--              record counters, and idempotent auto-apply reservations for the
--              ops-findings remediation flow (#2557). Additive expand-phase
--              migration only.

CREATE TABLE IF NOT EXISTS honua.ops_autonomy_policies (
    rule                        TEXT        PRIMARY KEY,
    mode                        SMALLINT    NOT NULL DEFAULT 0,
    max_auto_actions_per_window INTEGER     NOT NULL DEFAULT 1,
    window_seconds              INTEGER     NOT NULL DEFAULT 3600,
    max_blast_radius            INTEGER     NOT NULL DEFAULT 1,
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by                  TEXT        NOT NULL DEFAULT 'system',
    CONSTRAINT ops_autonomy_policies_valid_rule CHECK (length(rule) > 0),
    CONSTRAINT ops_autonomy_policies_valid_mode CHECK (mode IN (0, 1)),
    CONSTRAINT ops_autonomy_policies_valid_rate CHECK (max_auto_actions_per_window > 0),
    CONSTRAINT ops_autonomy_policies_valid_window CHECK (window_seconds > 0),
    CONSTRAINT ops_autonomy_policies_valid_blast CHECK (max_blast_radius > 0),
    CONSTRAINT ops_autonomy_policies_valid_actor CHECK (length(updated_by) > 0)
);

CREATE TABLE IF NOT EXISTS honua.ops_autonomy_settings (
    settings_id         TEXT        PRIMARY KEY,
    kill_switch_enabled BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by          TEXT        NOT NULL DEFAULT 'system',
    CONSTRAINT ops_autonomy_settings_valid_id CHECK (length(settings_id) > 0),
    CONSTRAINT ops_autonomy_settings_valid_actor CHECK (length(updated_by) > 0)
);

CREATE TABLE IF NOT EXISTS honua.ops_autonomy_rule_track_records (
    rule               TEXT        PRIMARY KEY,
    proposals_raised   BIGINT      NOT NULL DEFAULT 0,
    proposals_approved BIGINT      NOT NULL DEFAULT 0,
    proposals_rejected BIGINT      NOT NULL DEFAULT 0,
    auto_applied       BIGINT      NOT NULL DEFAULT 0,
    rolled_back        BIGINT      NOT NULL DEFAULT 0,
    failed             BIGINT      NOT NULL DEFAULT 0,
    first_activity_at  TIMESTAMPTZ NULL,
    last_activity_at   TIMESTAMPTZ NULL,
    CONSTRAINT ops_autonomy_track_valid_rule CHECK (length(rule) > 0),
    CONSTRAINT ops_autonomy_track_nonnegative_proposed CHECK (proposals_raised >= 0),
    CONSTRAINT ops_autonomy_track_nonnegative_approved CHECK (proposals_approved >= 0),
    CONSTRAINT ops_autonomy_track_nonnegative_rejected CHECK (proposals_rejected >= 0),
    CONSTRAINT ops_autonomy_track_nonnegative_auto CHECK (auto_applied >= 0),
    CONSTRAINT ops_autonomy_track_nonnegative_rollback CHECK (rolled_back >= 0),
    CONSTRAINT ops_autonomy_track_nonnegative_failed CHECK (failed >= 0)
);

CREATE TABLE IF NOT EXISTS honua.ops_autonomy_action_log (
    action_id              TEXT        PRIMARY KEY,
    finding_id             TEXT        NOT NULL,
    rule                   TEXT        NOT NULL,
    operation_class        TEXT        NOT NULL,
    action_discriminator   TEXT        NULL,
    blast_radius           INTEGER     NOT NULL DEFAULT 1,
    reserved_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    outcome                SMALLINT    NULL,
    execution_operation_id TEXT        NULL,
    outcome_message        TEXT        NULL,
    completed_at           TIMESTAMPTZ NULL,
    CONSTRAINT ops_autonomy_action_valid_action CHECK (length(action_id) > 0),
    CONSTRAINT ops_autonomy_action_valid_finding CHECK (length(finding_id) > 0),
    CONSTRAINT ops_autonomy_action_valid_rule CHECK (length(rule) > 0),
    CONSTRAINT ops_autonomy_action_valid_operation CHECK (length(operation_class) > 0),
    CONSTRAINT ops_autonomy_action_valid_blast CHECK (blast_radius > 0),
    CONSTRAINT ops_autonomy_action_valid_outcome CHECK (outcome IS NULL OR outcome IN (0, 1, 2)),
    CONSTRAINT ops_autonomy_action_unique_finding UNIQUE (finding_id)
);

CREATE INDEX IF NOT EXISTS idx_ops_autonomy_action_rule_window
    ON honua.ops_autonomy_action_log(rule, reserved_at DESC);

CREATE INDEX IF NOT EXISTS idx_ops_autonomy_action_finding
    ON honua.ops_autonomy_action_log(finding_id);

COMMENT ON TABLE honua.ops_autonomy_policies IS
    'Per-finding-rule graduated autonomy policy. mode=0 proposes only; mode=1 allows auto-apply when action and guardrail checks also pass.';
COMMENT ON TABLE honua.ops_autonomy_settings IS
    'Global ops autonomy kill switch and update metadata.';
COMMENT ON TABLE honua.ops_autonomy_rule_track_records IS
    'Per-rule proposal and auto-apply outcome counters used as the autonomy track record.';
COMMENT ON TABLE honua.ops_autonomy_action_log IS
    'Idempotent auto-apply reservation and outcome log keyed by finding id.';
