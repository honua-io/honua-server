-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 077_AddAlertChannelPauseState.sql
-- Description: Persists a per-channel delivery pause flag for the alert dispatch
--              pipeline (self-healing ops actuators, #2561). Alert delivery
--              channels were an enum discriminator only (alert_dispatch.channel_type)
--              with no per-channel row, so there was nowhere to persist a pause flag.
--              The dispatcher's claim query excludes rows whose channel is paused, so
--              pausing a channel stops its delivery claims and resuming restores them.
--              Additive expand-phase migration (CREATE TABLE only).

CREATE TABLE IF NOT EXISTS honua.alert_channel_state (
    channel_type SMALLINT PRIMARY KEY,
    is_paused BOOLEAN NOT NULL DEFAULT FALSE,
    paused_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT alert_channel_state_valid_channel CHECK (channel_type BETWEEN 1 AND 10)
);

COMMENT ON TABLE honua.alert_channel_state IS
    'Per-channel delivery control state for the alert dispatch pipeline. channel_type maps to AlertChannelType (1=webhook, 2=websocket, 3=email, 4=digest, 5=aws_sns, 6=azure_eventgrid, 7=slack, 8=microsoft_teams, 9=aws_sqs, 10=azure_eventhub). A row with is_paused=true stops the dispatcher from claiming that channel''s pending rows.';
