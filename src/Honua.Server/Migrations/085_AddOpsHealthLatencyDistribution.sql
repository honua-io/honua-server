-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 085_AddOpsHealthLatencyDistribution.sql
-- Description: Adds a mergeable latency-distribution sketch to the 1-minute serving-latency rollup (#2809).
--              Cross-replica cluster percentiles were previously derived from a request-weighted MEAN of
--              per-replica percentiles, which averages a sick replica out of the tail. Persisting a compact
--              fixed-bucket histogram per replica lets the cluster p95/p99 be recomputed from the SUMMED
--              distributions so a slow replica surfaces. Stored only on the 1-minute tier (the tier the live
--              snapshot and the serving-latency finding read); coarser downsampled tiers leave it NULL and
--              fall back to the documented weighted-mean approximation.
-- Dependencies: 077_CreateOpsHealthRollup.sql (creates honua.ops_health_rollup_latency).

ALTER TABLE honua.ops_health_rollup_latency
    ADD COLUMN IF NOT EXISTS distribution JSONB NULL;

COMMENT ON COLUMN honua.ops_health_rollup_latency.distribution IS
    'Mergeable fixed-bucket latency histogram as a JSON array of per-bucket sample counts (#2809). NULL on downsampled coarse tiers and on rows written before this migration.';
