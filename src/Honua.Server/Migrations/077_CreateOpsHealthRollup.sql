-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 077_CreateOpsHealthRollup.sql
-- Description: Persisted ops-health rollup store (#2553). Each replica's background sampler flushes
--              per-protocol serving-latency percentiles and ops vitals into the 1-minute tier keyed by
--              replica_id; coarser tiers (5-minute, hourly) are downsampled from it and every tier is
--              pruned to a hard retention cap (1-min x 24h, 5-min x 14d, hourly x 90d) so the footprint
--              stays bounded. Backs GET /api/v1/admin/observability/ops-health/history and the
--              cluster-wide serving-latency aggregation in the live ops-health snapshot.
-- Dependencies: none (standalone rollup tables in the honua schema).

-- Per-protocol serving-latency rollup. tier: 0=1-minute, 1=5-minute, 2=hourly. bucket_start is the
-- UTC bucket boundary. Percentiles/counts are the values observed by the replica's rolling-window
-- ServingLatencyAggregator at flush time (coarser tiers store request-count-weighted percentiles and
-- peak counts; exact historical percentiles remain in the optional Prometheus bundle).
CREATE TABLE IF NOT EXISTS honua.ops_health_rollup_latency (
    replica_id     TEXT             NOT NULL,
    tier           SMALLINT         NOT NULL,
    bucket_start   TIMESTAMPTZ      NOT NULL,
    protocol       TEXT             NOT NULL,
    request_count  BIGINT           NOT NULL DEFAULT 0,
    error_count    BIGINT           NOT NULL DEFAULT 0,
    p50_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
    p95_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
    p99_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
    max_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
    updated_at     TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
    CONSTRAINT ops_health_rollup_latency_pk PRIMARY KEY (replica_id, tier, bucket_start, protocol),
    CONSTRAINT ops_health_rollup_latency_valid_tier CHECK (tier IN (0, 1, 2))
);

CREATE INDEX IF NOT EXISTS idx_ops_health_rollup_latency_tier_bucket
    ON honua.ops_health_rollup_latency (tier, bucket_start);

COMMENT ON TABLE honua.ops_health_rollup_latency IS
    'Persisted per-replica serving-latency rollup with downsampled retention tiers (#2553).';
COMMENT ON COLUMN honua.ops_health_rollup_latency.tier IS
    '0=1-minute (retained 24h), 1=5-minute (14d), 2=hourly (90d).';

-- Per-replica ops vitals rollup: overall status, GP queue depth, alert-dispatch backlog / dead-letters,
-- and DB pool / cache / error vitals. Same tier/bucket keying as the latency table.
CREATE TABLE IF NOT EXISTS honua.ops_health_rollup_vitals (
    replica_id            TEXT             NOT NULL,
    tier                  SMALLINT         NOT NULL,
    bucket_start          TIMESTAMPTZ      NOT NULL,
    overall_status        TEXT             NOT NULL DEFAULT 'Unknown',
    gp_queue_total        INTEGER          NOT NULL DEFAULT 0,
    gp_queue_breakdown    JSONB            NOT NULL DEFAULT '{}',
    alert_pending         BIGINT           NULL,
    alert_dead_lettered   BIGINT           NULL,
    db_pool_utilization   DOUBLE PRECISION NULL,
    db_active_connections INTEGER          NOT NULL DEFAULT 0,
    cache_hit_ratio       DOUBLE PRECISION NOT NULL DEFAULT 0,
    error_rate            DOUBLE PRECISION NOT NULL DEFAULT 0,
    updated_at            TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
    CONSTRAINT ops_health_rollup_vitals_pk PRIMARY KEY (replica_id, tier, bucket_start),
    CONSTRAINT ops_health_rollup_vitals_valid_tier CHECK (tier IN (0, 1, 2))
);

CREATE INDEX IF NOT EXISTS idx_ops_health_rollup_vitals_tier_bucket
    ON honua.ops_health_rollup_vitals (tier, bucket_start);

COMMENT ON TABLE honua.ops_health_rollup_vitals IS
    'Persisted per-replica ops-vitals rollup (queue depth, alert backlog, DB/cache vitals) with retention tiers (#2553).';
COMMENT ON COLUMN honua.ops_health_rollup_vitals.tier IS
    '0=1-minute (retained 24h), 1=5-minute (14d), 2=hourly (90d).';
COMMENT ON COLUMN honua.ops_health_rollup_vitals.gp_queue_breakdown IS
    'GP queue depth by status and backend: {"<status>|<backend>": count} (flattened live-snapshot buckets). Every replica reports the same GLOBAL queue view, so cross-replica reads dedup with a per-key max; downsampling over time also stores the per-key peak.';
