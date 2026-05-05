-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Transactional outbox for feature-change CDC and webhook delivery (#692).
-- Outbox rows are written from the same call chain as feature mutations on
-- PostgreSQL, the only provider in this slice that supports transactional
-- outbox writes. SQL Server and DuckDB report SupportsTransactionalOutbox=false
-- and fall back to the post-commit publish + retry queue path. The
-- OutboxDispatcherBackgroundService claims rows with FOR UPDATE SKIP LOCKED,
-- publishes via IFeatureChangeEventPublisher, and marks them dispatched.
-- Failed rows are retried; rows that exceed the retry budget transition to
-- 'dead_lettered' and are surfaced via OutboxHealthCheck for operator action.

CREATE TABLE IF NOT EXISTS honua.feature_change_outbox (
    outbox_id        uuid        NOT NULL DEFAULT gen_random_uuid(),
    service_id       text        NOT NULL,
    layer_id         integer     NOT NULL,
    object_id        bigint      NOT NULL,
    operation        text        NOT NULL,
    protocol         text        NOT NULL,
    source_id        text,
    request_id       text        NOT NULL,
    event_id         text        NOT NULL,
    event_payload    jsonb       NOT NULL,
    status           text        NOT NULL DEFAULT 'pending',
    retry_count      integer     NOT NULL DEFAULT 0,
    last_error       text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    claimed_at       timestamptz,
    claim_node_id    text,
    claim_expires_at timestamptz,
    dispatched_at    timestamptz,
    CONSTRAINT feature_change_outbox_pkey PRIMARY KEY (outbox_id),
    CONSTRAINT feature_change_outbox_status_chk CHECK (
        status IN ('pending', 'claimed', 'dispatched', 'failed', 'dead_lettered')
    )
);

-- Dispatcher: locate claimable rows (pending or retriable) by oldest first.
-- Partial index keeps the working set tight and avoids scanning dispatched rows.
CREATE INDEX IF NOT EXISTS ix_fco_dispatch
    ON honua.feature_change_outbox (created_at)
    WHERE status IN ('pending', 'failed');

-- Recovery: locate rows whose claim lease has expired so they can be reset to pending.
CREATE INDEX IF NOT EXISTS ix_fco_claim_recovery
    ON honua.feature_change_outbox (claim_expires_at)
    WHERE status = 'claimed';

-- Health: surface dead-lettered rows for operator triage without scanning the full table.
CREATE INDEX IF NOT EXISTS ix_fco_dead_lettered
    ON honua.feature_change_outbox (created_at)
    WHERE status = 'dead_lettered';
