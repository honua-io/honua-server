-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 014_AddCloudAlertChannels.sql
-- Description: Extends alert dispatch with cloud provider channels (SNS, Event Grid,
--              Slack, Teams, SQS, Event Hub) and adds incident lifecycle tracking
--              (Started/Ongoing/Ended) to alert events.

-- Widen the channel_type CHECK constraint to accept cloud provider values.
ALTER TABLE honua.alert_dispatch
    DROP CONSTRAINT IF EXISTS alert_dispatch_valid_channel;

ALTER TABLE honua.alert_dispatch
    ADD CONSTRAINT alert_dispatch_valid_channel CHECK (channel_type BETWEEN 1 AND 10);

-- Add incident lifecycle columns to alert events.
ALTER TABLE honua.alert_events
    ADD COLUMN IF NOT EXISTS incident_status SMALLINT NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS incident_duration_ms BIGINT NOT NULL DEFAULT 0;

ALTER TABLE honua.alert_events
    DROP CONSTRAINT IF EXISTS alert_events_valid_incident_status;

ALTER TABLE honua.alert_events
    ADD CONSTRAINT alert_events_valid_incident_status CHECK (incident_status IN (1, 2, 3));

-- Update documentation
COMMENT ON COLUMN honua.alert_dispatch.channel_type IS '1=webhook, 2=websocket, 3=email, 4=digest, 5=aws_sns, 6=azure_eventgrid, 7=slack, 8=microsoft_teams, 9=aws_sqs, 10=azure_eventhub';
COMMENT ON COLUMN honua.alert_events.incident_status IS '1=started, 2=ongoing, 3=ended';
COMMENT ON COLUMN honua.alert_events.incident_duration_ms IS 'Duration of the incident in milliseconds from when it started';
